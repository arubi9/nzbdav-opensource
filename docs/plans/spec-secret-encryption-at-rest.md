# Spec: Secret Encryption at Rest (V5)

*Approved 2026-04-06*

Config secrets in NZBDAV — usenet provider passwords, Radarr/Sonarr API keys, the SABnzbd-compatible API key, the HMAC signing key for `.strm` stream tokens, and the bcrypt-hashed WebDAV password — are stored as plaintext `TEXT` values in the `ConfigItems` table. Anyone who can read the SQLite file (stolen backup, exposed cloud backup, leaked volume) can trivially harvest everything.

This spec adds per-row encryption for sensitive `ConfigItem` values using AES-GCM with a master key supplied via environment variable.

---

## Threat Model

**In scope:**
- Stolen SQLite database file (e.g., leaked `/config` volume, cloud backup exfiltration).
- Stolen PostgreSQL dump from multi-node deployments.
- Read access to the filesystem without access to the running process (e.g., a different container on the same host with an errant volume mount).

**Out of scope:**
- Host compromise (root access to the running process — can read env vars via `/proc/<pid>/environ`).
- Live memory scraping.
- Operator who loses the master key (explicit: we tell them to delete the DB and reconfigure).

The design hinges on the master key never touching disk in a form that travels with the database file. An env var satisfies this: it's not in the DB, it's not in the config volume, and it only exists in the process's address space.

---

## Design Decisions

| Question | Decision |
|---|---|
| Where does the master key live? | Environment variable `NZBDAV_MASTER_KEY` (base64-encoded 32 bytes, AES-256) |
| What gets encrypted? | Per-column on `ConfigItems.ConfigValue`, gated by an explicit `IsEncrypted` boolean flag per row, with a hardcoded `SensitiveConfigKeys` set driving which keys get the flag |
| Mandatory or opt-in? | New installs: strict (key required). Existing installs: gentle (loud warning, opt-in) |
| What about `webdav.pass`? | Encrypted alongside other secrets for consistency (comment documents that it's already a bcrypt hash) |

---

## What's Considered Sensitive

```csharp
// backend/Config/SensitiveConfigKeys.cs

public static class SensitiveConfigKeys
{
    /// <summary>
    /// ConfigItem keys whose values must be encrypted at rest.
    ///
    /// Inclusion rule: any value that is either a recoverable credential
    /// (password, API key, HMAC secret) OR is shaped like one to a future
    /// reviewer. `webdav.pass` is included even though it stores a bcrypt
    /// hash — the inclusion is for consistency and to prevent "why isn't
    /// this one encrypted?" drive-by PRs. The absence of other hash-type
    /// fields is also intentional; check here before adding one.
    /// </summary>
    public static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "usenet.providers",   // JSON blob containing provider Pass fields
        "arr.instances",      // JSON blob containing Radarr/Sonarr ApiKey fields
        "api.key",            // SABnzbd-compatible REST API key
        "api.strm-key",       // HMAC signing key for .strm stream tokens
        "webdav.pass",        // bcrypt hash — encrypted for consistency; see above
    };

    public static bool IsSensitive(string configName) => Keys.Contains(configName);
}
```

Adding a new secret-bearing config key means adding one line to this set. No other change required — the encryption path picks it up automatically via the `IsEncrypted` flag on save.

---

## Ciphertext Format

All encrypted values use the self-describing format:

```
v1:<base64url(nonce ‖ ciphertext ‖ tag)>
```

- **`v1:`** — literal 3-byte prefix. Version marker for forward compatibility. A plaintext value that happens to start with `v1:` is impossible in practice (no config key we care about produces that prefix) and would be caught by explicit input validation during save.
- **Nonce** — 12 random bytes, generated fresh per encryption call via `RandomNumberGenerator.GetBytes(12)`. The "never reuse a nonce under the same key" invariant that AES-GCM depends on is enforced by construction — nonces are never stored, counted, or derived; each call draws fresh randomness.
- **Ciphertext** — same length as plaintext, produced by `AesGcm.Encrypt(nonce, plaintext, ciphertext, tag)`.
- **Tag** — 16 bytes, AES-GCM's authentication tag. Also produced by `AesGcm.Encrypt`. A wrong key causes `AesGcm.Decrypt` to throw `CryptographicException` on tag mismatch; we catch this to trigger the old-key fallback path during rotation.
- **base64url** — chosen over standard base64 to avoid `+` and `/` characters that can confuse shell tools and URL-encoding. Padding characters (`=`) are stripped.

**Encoding:** UTF-8 for the plaintext `ConfigValue` bytes before encryption.

### Rationale for the prefix

The prefix makes encrypted values unambiguously identifiable without relying on the `IsEncrypted` column alone. This is a defense-in-depth check: if the column flag ever drifts from the value (e.g., due to a buggy migration), we can detect the mismatch and fail loudly instead of silently returning ciphertext as plaintext to the app.

---

## Schema Change

Add one column to the `ConfigItems` table:

```csharp
// backend/Database/Models/ConfigItem.cs

public class ConfigItem
{
    public string ConfigName { get; set; } = null!;
    public string ConfigValue { get; set; } = null!;

    /// <summary>
    /// True when ConfigValue is encrypted with the v1 format
    /// (v1:base64url(nonce||ciphertext||tag)) using the
    /// NZBDAV_MASTER_KEY env var as the AES-256-GCM key.
    /// </summary>
    public bool IsEncrypted { get; set; }
}
```

### EF Core Migration

```csharp
// backend/Database/Migrations/NNNNNNNNNNN_AddIsEncryptedToConfigItems.cs

migrationBuilder.AddColumn<bool>(
    name: "IsEncrypted",
    table: "ConfigItems",
    type: "INTEGER",  // SQLite; provider translates for Postgres
    nullable: false,
    defaultValue: false);
```

The column defaults to `false` so every existing row is automatically treated as plaintext until the migration pass runs.

---

## Encryption Service

New class owns the crypto primitives. One instance, singleton-scoped.

```csharp
// backend/Services/ConfigEncryptionService.cs

public sealed class ConfigEncryptionService : IDisposable
{
    private const string FormatPrefix = "v1:";
    private const int NonceSize = 12;   // AES-GCM standard
    private const int TagSize = 16;     // AES-GCM standard
    private const int KeySize = 32;     // AES-256

    // Key bytes kept as fields. They are NOT zeroed on disposal — the
    // GC owns byte[] memory and CryptographicOperations.ZeroMemory on a
    // managed array only scrubs that one reference, not any copies the
    // runtime may have made during JIT or GC compaction. The stated
    // threat model is stolen files, not memory scraping, so this is
    // acceptable. If the threat model ever expands to in-process
    // attackers, switch to SecureString or a native memory buffer.
    private readonly byte[]? _primaryKey;
    private readonly byte[]? _oldKey;
    private bool _disposed;

    public bool IsKeyConfigured => _primaryKey != null;

    public ConfigEncryptionService()
    {
        _primaryKey = LoadKey("NZBDAV_MASTER_KEY");
        _oldKey = LoadKey("NZBDAV_MASTER_KEY_OLD");
    }

    public string Encrypt(string plaintext)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConfigEncryptionService));
        if (_primaryKey is null)
            throw new InvalidOperationException(
                "Cannot encrypt: NZBDAV_MASTER_KEY is not set.");

        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        // AesGcm is IDisposable. Create per-call — avoids long-lived
        // native handles, and AES-GCM setup is cheap compared to the
        // database round-trip we're already paying for.
        using (var aes = new System.Security.Cryptography.AesGcm(_primaryKey, TagSize))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        // Pack nonce || ciphertext || tag into one buffer, base64url-encode.
        var packed = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, packed, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceSize + ciphertext.Length, TagSize);

        return FormatPrefix + Base64UrlEncode(packed);
    }

    /// <summary>
    /// Decrypts ciphertext. Tries the primary key first, then the old key
    /// (if present) on tag mismatch. Returns (plaintext, usedOldKey) so
    /// the caller can trigger rotation.
    /// </summary>
    public (string plaintext, bool usedOldKey) Decrypt(string ciphertext)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConfigEncryptionService));
        if (!IsEncryptedFormat(ciphertext))
            throw new InvalidOperationException(
                "Cannot decrypt value without the v1: prefix.");

        var packed = Base64UrlDecode(ciphertext.AsSpan(FormatPrefix.Length));
        if (packed.Length < NonceSize + TagSize)
            throw new System.Security.Cryptography.CryptographicException(
                "Ciphertext too short to contain nonce and tag.");

        var nonce = packed.AsSpan(0, NonceSize);
        var cipherBody = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize, TagSize);
        var plaintextBytes = new byte[cipherBody.Length];

        // Try primary key first.
        if (_primaryKey is not null && TryDecrypt(_primaryKey, nonce, cipherBody, tag, plaintextBytes))
            return (System.Text.Encoding.UTF8.GetString(plaintextBytes), usedOldKey: false);

        // Fall back to old key on authentication tag mismatch.
        if (_oldKey is not null && TryDecrypt(_oldKey, nonce, cipherBody, tag, plaintextBytes))
            return (System.Text.Encoding.UTF8.GetString(plaintextBytes), usedOldKey: true);

        throw new System.Security.Cryptography.CryptographicException(
            "Failed to decrypt config value with any configured master key. " +
            "If this row was encrypted with an older key, set NZBDAV_MASTER_KEY_OLD " +
            "alongside the current NZBDAV_MASTER_KEY and restart to rotate.");
    }

    private static bool TryDecrypt(
        byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag, Span<byte> plaintext)
    {
        try
        {
            using var aes = new System.Security.Cryptography.AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>True if the value matches the v1 format marker.</summary>
    public static bool IsEncryptedFormat(string value)
        => value.StartsWith(FormatPrefix, StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Best-effort key scrubbing. See comment on the key fields.
        if (_primaryKey is not null) Array.Clear(_primaryKey);
        if (_oldKey is not null) Array.Clear(_oldKey);
    }

    private static byte[]? LoadKey(string envVarName) { /* unchanged from above */ }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(ReadOnlySpan<char> s)
    {
        var padded = new string(s).Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
```

**Why a dedicated service, not a value converter?** EF Core value converters run on every load/save regardless of whether the row should be encrypted. With an explicit `IsEncrypted` flag, we need branching logic ("encrypt iff sensitive on save", "decrypt iff flag on load") that doesn't fit the converter model cleanly. A service called explicitly from `ConfigManager.LoadConfig` and `ConfigManager.UpdateValues` is simpler.

**Disposal note.** `AesGcm` instances are created per-call inside `using` blocks — they hold native handles that need prompt release. The `ConfigEncryptionService` itself implements `IDisposable` to best-effort-scrub the key byte arrays on shutdown; this is defense-in-depth, not a guarantee (see the comment on the key fields). DI registers the service as a singleton, so disposal happens during host shutdown.

---

## Startup Flow

In `Program.cs`, after `configManager.LoadConfig()`, wire the startup check:

```csharp
// Load encryption service and perform startup validation
var encryptionService = new ConfigEncryptionService();
// Register as singleton so ConfigManager's UpdateValues can use it.
builder.Services.AddSingleton(encryptionService);

// Run startup check BEFORE any traffic is accepted.
await StartupEncryptionCheck.RunAsync(dbContext, encryptionService);
```

```csharp
// backend/Services/StartupEncryptionCheck.cs

public static class StartupEncryptionCheck
{
    public static async Task RunAsync(
        DavDatabaseContext db,
        ConfigEncryptionService encryption)
    {
        var allConfig = await db.ConfigItems.ToListAsync();

        var hasEncryptedRows = allConfig.Any(c => c.IsEncrypted);
        var isFreshInstall = allConfig.Count == 0;

        if (!encryption.IsKeyConfigured)
        {
            if (hasEncryptedRows)
            {
                throw new InvalidOperationException(
                    "Found encrypted config but NZBDAV_MASTER_KEY is not set. " +
                    "The master key is required to decrypt existing config. " +
                    "If you lost the key, delete the config database and reconfigure.");
            }

            if (isFreshInstall)
            {
                throw new InvalidOperationException(
                    "NZBDAV_MASTER_KEY is required for new installations. " +
                    "Generate one with: openssl rand -base64 32 " +
                    "See docs/setup-guide.md for details.");
            }

            Log.Warning(
                "====================================================================");
            Log.Warning(
                "  Config secrets are stored in plaintext.");
            Log.Warning(
                "  Set NZBDAV_MASTER_KEY to enable encryption at rest.");
            Log.Warning(
                "  See docs/setup-guide.md#encryption for details.");
            Log.Warning(
                "====================================================================");
            return;
        }

        // Key is configured. Run migration + rotation in a single transaction.
        await MigrateAndRotateAsync(db, encryption);
    }

    private static async Task MigrateAndRotateAsync(
        DavDatabaseContext db,
        ConfigEncryptionService encryption)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var migrated = 0;
            var rotated = 0;

            foreach (var row in await db.ConfigItems.ToListAsync())
            {
                // Case 1: plaintext row matching a sensitive key → encrypt it.
                if (!row.IsEncrypted && SensitiveConfigKeys.IsSensitive(row.ConfigName))
                {
                    row.ConfigValue = encryption.Encrypt(row.ConfigValue);
                    row.IsEncrypted = true;
                    migrated++;
                    continue;
                }

                // Case 2: encrypted row → try decrypt. If it used the old key,
                // re-encrypt with the primary key (key rotation).
                if (row.IsEncrypted)
                {
                    var (plaintext, usedOldKey) = encryption.Decrypt(row.ConfigValue);
                    if (usedOldKey)
                    {
                        row.ConfigValue = encryption.Encrypt(plaintext);
                        rotated++;
                    }
                }
            }

            if (migrated > 0)
            {
                // Write the post-migration marker so the UI can prompt the
                // operator to rotate historical-backup credentials. Only set
                // it on the FIRST migration — subsequent startups where
                // migrated is 0 leave it alone. Presence of the row is the
                // signal the banner keys off.
                var alreadyHasMarker = await db.ConfigItems
                    .AnyAsync(c => c.ConfigName == "encryption.migration-completed-at");
                if (!alreadyHasMarker)
                {
                    db.ConfigItems.Add(new ConfigItem
                    {
                        ConfigName = "encryption.migration-completed-at",
                        ConfigValue = DateTime.UtcNow.ToString("O"),
                        IsEncrypted = false,
                    });
                }
            }

            if (migrated > 0 || rotated > 0)
                await db.SaveChangesAsync();
            await transaction.CommitAsync();

            if (migrated > 0)
            {
                Log.Information("Encrypted {Count} existing config secrets on startup", migrated);
                Log.Warning(
                    "===========================================================");
                Log.Warning(
                    "  Historical backups of your config database (if any) are");
                Log.Warning(
                    "  still PLAINTEXT. Rotate usenet provider passwords,");
                Log.Warning(
                    "  Radarr/Sonarr API keys, and the NZBDAV API key NOW if");
                Log.Warning(
                    "  there is any chance a pre-migration backup is exposed.");
                Log.Warning(
                    "===========================================================");
            }
            if (rotated > 0)
                Log.Information(
                    "Key rotation complete: {Count} rows re-encrypted with primary key. " +
                    "NZBDAV_MASTER_KEY_OLD can now be unset.",
                    rotated);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
```

### Startup Decision Table

| `NZBDAV_MASTER_KEY` set? | `ConfigItems` empty? | Any `IsEncrypted=true` rows? | Outcome |
|---|---|---|---|
| Yes | — | — | Normal operation. Run migration + rotation. |
| No | Yes | — | **FATAL** — new install requires key |
| No | No | Yes | **FATAL** — can't decrypt existing rows |
| No | No | No | Warn loudly, run in legacy plaintext mode |

---

## Key Rotation Protocol

**Operator workflow:**

1. Generate a new key: `openssl rand -base64 32`
2. In the container environment:
   - Set `NZBDAV_MASTER_KEY_OLD` to the *current* key's value
   - Set `NZBDAV_MASTER_KEY` to the new key's value
3. Restart the container.
4. On startup, the rotation pass runs inside `MigrateAndRotateAsync`:
   - Each `IsEncrypted=true` row is decrypted. Primary key tried first.
   - On tag mismatch, falls back to old key.
   - On old-key success, re-encrypts with the primary key and saves.
5. Log line `"Key rotation complete: N rows re-encrypted."` confirms success.
6. Operator removes `NZBDAV_MASTER_KEY_OLD` from the environment and restarts once more.

**Crash during rotation:** The single transaction rolls back. No split state. Next startup retries from the same point. Both env vars must still be present for the retry to succeed.

**Why self-healing works without a version marker:** Each row is individually decryptable with whichever key successfully produces its tag. There's no global state to get out of sync. The only thing the operator has to do is keep the old key around until the rotation pass succeeds.

---

## ConfigManager Integration

`ConfigManager.LoadConfig` decrypts sensitive rows into the in-memory dictionary. `ConfigManager.UpdateValues` encrypts sensitive rows before writing.

```csharp
// backend/Config/ConfigManager.cs (modified)

public class ConfigManager
{
    private readonly ConfigEncryptionService _encryption;
    // ... existing fields ...

    public ConfigManager(ConfigEncryptionService encryption)
    {
        _encryption = encryption;
    }

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                string value;
                if (configItem.IsEncrypted)
                {
                    var (plaintext, usedOldKey) = _encryption.Decrypt(configItem.ConfigValue);
                    value = plaintext;

                    // If LoadConfig is called again at runtime (e.g. hot-reload
                    // after OnConfigChanged), the StartupEncryptionCheck rotation
                    // pass has already completed and won't run again until the
                    // next restart. Log loudly so the operator knows rotation
                    // isn't complete and NZBDAV_MASTER_KEY_OLD still needs to
                    // stay set until they can restart.
                    if (usedOldKey)
                    {
                        Log.Warning(
                            "Config key '{Name}' was decrypted with NZBDAV_MASTER_KEY_OLD " +
                            "outside the startup rotation pass. A full rotation requires " +
                            "a restart; keep NZBDAV_MASTER_KEY_OLD set until then.",
                            configItem.ConfigName);
                    }
                }
                else
                {
                    value = configItem.ConfigValue;
                }
                _config[configItem.ConfigName] = value;
            }
        }
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        // Mark and encrypt sensitive rows before handing them to storage.
        foreach (var item in configItems)
        {
            if (!SensitiveConfigKeys.IsSensitive(item.ConfigName) || !_encryption.IsKeyConfigured)
                continue;

            // Double-encryption defense. If the caller hands us a value that
            // already looks encrypted (has the v1: prefix), something has
            // gone wrong — either the caller double-wrapped, or a legitimate
            // plaintext value starts with v1: and would get misclassified.
            // Either way, silently skipping encryption leaves the data in
            // an inconsistent state (IsEncrypted=true with undetectable
            // plaintext-or-ciphertext contents). Fail loudly instead.
            if (ConfigEncryptionService.IsEncryptedFormat(item.ConfigValue))
            {
                throw new InvalidOperationException(
                    $"Double-encryption detected for config key '{item.ConfigName}'. " +
                    $"Value already has the v1: prefix before the encryption pass. " +
                    $"This usually means the caller wrapped the value twice, or a " +
                    $"plaintext value happens to start with 'v1:' and cannot be " +
                    $"distinguished from ciphertext. Reject the write.");
            }

            item.ConfigValue = _encryption.Encrypt(item.ConfigValue);
            item.IsEncrypted = true;
        }

        // ... existing in-memory cache update logic ...
        // NB: the in-memory _config still holds the DECRYPTED values for
        // app consumption. Callers of Get*() get plaintext as today.

        // (Existing raises OnConfigChanged for subscribers)
    }
}
```

Note: because `UpdateValues` is also called from the settings UI, the in-memory cache must continue to hold plaintext. The encryption only affects what hits the database, not what `GetApiKey()` / `GetUsenetProviderConfig()` return at runtime.

---

## Encryption Status API

New read-only endpoint for the web UI banner.

```csharp
// backend/Api/Controllers/EncryptionStatus/EncryptionStatusController.cs

[ApiController]
[Route("api/encryption-status")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class EncryptionStatusController(
    DavDatabaseClient dbClient,
    ConfigEncryptionService encryption) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // Materialize the sensitive-keys set into a List<string> BEFORE the
        // query. EF Core translates List<string>.Contains to SQL IN (...);
        // HashSet<string>.Contains may fall back to client evaluation or
        // throw depending on provider version. Local variable forces the
        // parameterized IN clause. See:
        // https://learn.microsoft.com/en-us/ef/core/querying/client-eval
        var sensitiveKeys = SensitiveConfigKeys.Keys.ToList();

        // Count only plaintext rows that SHOULD be encrypted —
        // "unencrypted plaintext secrets in the DB".
        var plaintextSecretsCount = await dbClient.Ctx.ConfigItems
            .Where(c => !c.IsEncrypted && sensitiveKeys.Contains(c.ConfigName))
            .CountAsync();

        var migrationCompletedAt = await dbClient.Ctx.ConfigItems
            .Where(c => c.ConfigName == "encryption.migration-completed-at")
            .Select(c => c.ConfigValue)
            .FirstOrDefaultAsync();

        var postMigrationAcknowledged = await dbClient.Ctx.ConfigItems
            .AnyAsync(c => c.ConfigName == "encryption.post-migration-acknowledged");

        return Ok(new
        {
            keySet = encryption.IsKeyConfigured,
            plaintextSecretsCount,
            bannerSeverity = encryption.IsKeyConfigured ? "none"
                           : plaintextSecretsCount > 0 ? "warning" : "info",
            migrationCompletedAt,
            postMigrationAcknowledged,
        });
    }

    [HttpPost("acknowledge-post-migration")]
    public async Task<IActionResult> AcknowledgePostMigration()
    {
        var existing = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == "encryption.post-migration-acknowledged");
        if (existing is null)
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "encryption.post-migration-acknowledged",
                ConfigValue = DateTime.UtcNow.ToString("O"),
                IsEncrypted = false,
            });
            await dbClient.Ctx.SaveChangesAsync();
        }
        return NoContent();
    }
}
```

**Authentication note:** This endpoint is behind `ApiKeyAuthFilter`, NOT anonymous. That's safe because `ApiKeyAuthFilter` falls back to the `FRONTEND_BACKEND_API_KEY` env var (see `ConfigManager.GetApiKey` line 70) which is *independent* of `NZBDAV_MASTER_KEY`. The frontend-to-backend auth path always works even when the master key is not set. **Do not "tighten" this fallback without revisiting this banner** — removing the env var fallback would break the banner's ability to warn users that they need to set the master key.

An inline comment in `GetApiKey()` will reference this spec so the dependency is visible.

---

## Web UI Banner

In `frontend/app/routes/settings/route.tsx` (or a shared layout), poll `/api/encryption-status` on mount and render a banner:

```tsx
{encStatus && !encStatus.keySet && (
  <div className="banner banner-warning">
    ⚠️ <strong>Config secrets are stored in plaintext.</strong>
    {encStatus.plaintextSecretsCount > 0 && (
      <> {encStatus.plaintextSecretsCount} sensitive value(s) are unencrypted. </>
    )}
    Set <code>NZBDAV_MASTER_KEY</code> in your environment to enable
    encryption at rest. <a href="/docs/setup-guide.md#encryption">Learn more</a>
  </div>
)}
```

The banner is visible on every settings page load while the key is unset. No dismiss button — the warning is by design persistent until the operator acts on it.

---

## Setup Guide Updates

Add a new section to `docs/setup-guide.md`:

```markdown
## Encryption at Rest

NZBDAV encrypts sensitive config values (usenet provider passwords,
Radarr/Sonarr API keys, the SABnzbd API key, and the .strm signing
key) using AES-256-GCM. The master key is supplied via the
`NZBDAV_MASTER_KEY` environment variable.

### New Installation

Generate a key and pass it to the container:

    openssl rand -base64 32
    # Example output: Rk0ZJ3c0xtr5yv4J4Qr6r7xT3r2sYmfXKjXzZ7dP6Ss=

Add to your `docker run` command:

    -e NZBDAV_MASTER_KEY="Rk0ZJ3c0xtr5yv4J4Qr6r7xT3r2sYmfXKjXzZ7dP6Ss="

Or in `docker-compose.yml`:

    environment:
      NZBDAV_MASTER_KEY: "Rk0ZJ3c0xtr5yv4J4Qr6r7xT3r2sYmfXKjXzZ7dP6Ss="

**Save this key somewhere safe (password manager, secrets vault).
If you lose it, you lose access to your encrypted config and must
reconfigure from scratch.**

### Upgrading an Existing Installation

On first startup after upgrade, the server logs a warning if
`NZBDAV_MASTER_KEY` is not set:

    WARNING: Config secrets are stored in plaintext.
    Set NZBDAV_MASTER_KEY to enable encryption at rest.

Existing installs continue to work without the key — encryption
is opt-in for upgrades. To enable:

1. Generate a key: `openssl rand -base64 32`
2. Add `NZBDAV_MASTER_KEY=<key>` to your environment
3. Restart the container
4. On startup, existing plaintext secrets are automatically
   encrypted in place. Look for:

       Encrypted N existing config secrets on startup

**CRITICAL: Rotate your credentials after enabling encryption.**
If you have ANY backup of your config database (cloud backup,
volume snapshot, local copy) taken before you set
`NZBDAV_MASTER_KEY`, that backup still contains your secrets in
plaintext. Enabling encryption does not retroactively protect
historical backups. After step 4, immediately:

1. Change your usenet provider password (on the provider's website)
2. Regenerate your Radarr API key (Radarr → Settings → General →
   Security → API Key → "Show" → Generate new)
3. Regenerate your Sonarr API key (same path)
4. Regenerate your NZBDAV SABnzbd API key (NZBDAV Settings →
   SABnzbd → Regenerate)
5. Update Radarr/Sonarr's download client settings with the new
   NZBDAV API key
6. Update any `.strm` files Jellyfin uses — the stream-signing
   key rotates too, which invalidates old tokens. The Jellyfin
   library scan task will regenerate them automatically on next
   run.

NZBDAV shows a persistent warning banner in the settings UI until
you dismiss it, confirming the credential rotation is complete.
**Do not dismiss until you have actually rotated.**

### Rotating the Master Key

1. Generate a new key.
2. Set BOTH env vars for the next restart:
   - `NZBDAV_MASTER_KEY_OLD` = the *current* key
   - `NZBDAV_MASTER_KEY` = the *new* key
3. Restart. The server re-encrypts all rows with the new key
   and logs:

       Key rotation complete: N rows re-encrypted with primary key.

4. Remove `NZBDAV_MASTER_KEY_OLD` from the environment.
5. Restart once more.

### Losing the Master Key

If you lose `NZBDAV_MASTER_KEY` and have no backup, encrypted
config is unrecoverable. You must delete the config database
and reconfigure from scratch:

    docker stop nzbdav
    docker volume rm nzbdav-config  # or rm /path/to/config/config.db
    docker start nzbdav

This is by design. The master key is the only thing protecting
your secrets from stolen backups — if the app could recover
without it, so could an attacker.
```

---

## Files

### New

| File | Purpose |
|---|---|
| `backend/Config/SensitiveConfigKeys.cs` | Allowlist of sensitive config keys (5 entries) with rationale comment |
| `backend/Services/ConfigEncryptionService.cs` | AES-GCM primitives, key loading, encrypt/decrypt, `IDisposable` with best-effort key scrubbing |
| `backend/Services/StartupEncryptionCheck.cs` | Startup decision table + migration/rotation pass + first-migration marker |
| `backend/Api/Controllers/EncryptionStatus/EncryptionStatusController.cs` | GET `/api/encryption-status` + POST `/api/encryption-status/acknowledge-post-migration` |
| `backend/Database/Migrations/NNNNNNNNNNN_AddIsEncryptedToConfigItems.cs` | EF Core migration adding `IsEncrypted` column |

### Modified

| File | Change |
|---|---|
| `backend/Database/Models/ConfigItem.cs` | Add `IsEncrypted` property |
| `backend/Database/DavDatabaseContext.cs` | Configure `IsEncrypted` column (default false) |
| `backend/Config/ConfigManager.cs` | Inject `ConfigEncryptionService`; decrypt on load, encrypt on save; log warning on runtime old-key decryption; **throw** on double-encryption; comment on `GetApiKey` fallback dependency |
| `backend/Program.cs` | Register `ConfigEncryptionService` singleton; call `StartupEncryptionCheck.RunAsync` before the web host starts |
| `frontend/app/routes/settings/route.tsx` | Fetch `/api/encryption-status`; render plaintext-warning banner when `keySet === false`; render post-migration banner when `migrationCompletedAt && !postMigrationAcknowledged`; wire dismiss button to the acknowledge endpoint |
| `docs/setup-guide.md` | New "Encryption at Rest" section with upgrade checklist that leads with credential rotation |

---

## Testing Strategy

### Unit tests

**`ConfigEncryptionService`:**
1. Round-trip: encrypt → decrypt returns the original plaintext
2. Each call produces a different ciphertext (nonce randomness)
3. Tag mismatch: decrypting with wrong key throws `CryptographicException`
4. Wrong key + old key fallback: returns `(plaintext, usedOldKey: true)`
5. Format prefix: `IsEncryptedFormat("v1:...")` is true, `IsEncryptedFormat("hello")` is false
6. Invalid base64 in env var: throws `InvalidOperationException` with actionable message
7. Wrong key length in env var: throws `InvalidOperationException` with actionable message
8. Truncated ciphertext (< NonceSize + TagSize bytes) throws `CryptographicException`
9. Tampered ciphertext (flip one byte in the body) fails tag verification under both keys
10. `Dispose()` is idempotent and subsequent calls throw `ObjectDisposedException`

**`SensitiveConfigKeys`:**
1. `IsSensitive("usenet.providers")` → true
2. `IsSensitive("cache.max-size-gb")` → false
3. `IsSensitive("webdav.pass")` → true (regression guard for the intentional inclusion)

**`StartupEncryptionCheck`:**
1. Fresh DB + no key → throws with "new installation" message
2. DB with encrypted rows + no key → throws with "lost the key" message
3. DB with plaintext rows + no key (upgrade path) → succeeds, logs warning
4. DB with plaintext secrets + key set → migrates, rows are encrypted + flagged
5. DB with encrypted rows + key set → no-op
6. DB with rows encrypted by old key + primary key set → throws (rotation not possible without old key)
7. DB with rows encrypted by old key + both keys set → rotates to primary, updates rows
8. Mid-rotation simulated crash → transaction rollback, DB unchanged
9. First successful migration writes `encryption.migration-completed-at` row
10. Second startup after migration does NOT overwrite `encryption.migration-completed-at`
11. Key rotation on an already-migrated DB does NOT write or update the migration-completed-at marker

**`ConfigManager`:**
1. `UpdateValues` called with a sensitive-keyed plaintext value → row is encrypted + flagged
2. `UpdateValues` called with a non-sensitive key → row stays plaintext + not flagged
3. `UpdateValues` called with a sensitive-keyed value that already has the `v1:` prefix → **throws `InvalidOperationException`** (double-encryption guard regression test)
4. `LoadConfig` run after a partial rotation (some rows encrypted with primary, some with old) → logs a warning for each row decrypted with the old key
5. `LoadConfig` on a DB with encrypted rows and no key configured → throws (consistent with `StartupEncryptionCheck`)

**`EncryptionStatusController`:**
1. GET with key set → `keySet: true`, `plaintextSecretsCount: 0` after migration
2. GET with key unset and plaintext secrets present → `keySet: false`, `plaintextSecretsCount > 0`, `bannerSeverity: "warning"`
3. GET after migration but before acknowledgment → `migrationCompletedAt` populated, `postMigrationAcknowledged: false`
4. POST `/acknowledge-post-migration` → writes the ack row, subsequent GET returns `postMigrationAcknowledged: true`
5. POST `/acknowledge-post-migration` twice → second call is a no-op (idempotent)
6. Query uses `List<string>.Contains` → regression test that verifies the generated SQL contains `IN (...)` (can use EF Core's `ToQueryString()` to inspect)

### Integration tests

1. Start the app with `NZBDAV_MASTER_KEY` unset and an empty DB → fails to start
2. Start with key set and empty DB → starts normally
3. Start with key set, write secrets via the settings API, inspect the DB → `ConfigValue` column begins with `v1:`, `IsEncrypted` is true
4. Inspect non-sensitive rows (e.g., `cache.max-size-gb`) → still plaintext, `IsEncrypted` false
5. Restart the app with the same key → secrets decrypt correctly, app functions normally
6. Restart with the wrong key → fails to start with actionable error
7. Rotation: write secrets with key A, restart with key A as OLD and key B as primary → secrets re-encrypt, new reads succeed
8. `/api/encryption-status` returns `keySet: true` when key is set, `false` otherwise

### Manual verification

1. `sqlite3 config.db "SELECT ConfigName, substr(ConfigValue, 1, 20), IsEncrypted FROM ConfigItems"` — sensitive rows show `v1:...` prefix, non-sensitive rows show original plaintext
2. Settings UI with the key unset shows the persistent warning banner
3. Settings UI with the key set shows no banner
4. Setup guide encryption section is accurate and commands work as documented

---

## Rollout Notes

- **Breaking change for new installs only.** Anyone installing NZBDAV after this change must set `NZBDAV_MASTER_KEY`. The setup guide leads with this.
- **Non-breaking for existing installs.** Upgrades see the warning banner and log warnings but continue operating. No data migration runs until the operator opts in by setting the key.
- **Historical backups remain plaintext — operators MUST rotate credentials after opting in.** This is the critical caveat. Backups captured before encryption was enabled still contain plaintext provider passwords, Radarr/Sonarr API keys, the SABnzbd API key, and the `.strm` HMAC signing key. If those backups are ever exposed, this entire feature provides zero protection for anything in them. The post-migration banner (see below) makes this explicit and un-ignorable, and the setup guide's "Upgrading an Existing Installation" section leads with the credential rotation checklist.
- **PostgreSQL multi-node mode is supported unchanged.** The encryption happens in .NET before the value reaches the provider, so PostgreSQL sees the same ciphertext bytes SQLite does.

### Post-Migration Banner (one-time)

After `StartupEncryptionCheck.MigrateAndRotateAsync` successfully encrypts plaintext rows for the first time (the `migrated > 0` branch), it writes a `ConfigItem` row:

```
ConfigName = "encryption.migration-completed-at"
ConfigValue = <ISO8601 UTC timestamp of the migration>
IsEncrypted = false
```

The settings UI treats this row as the trigger for a **second banner** — separate from the "plaintext warning" banner — that is shown until the operator acknowledges:

> ✅ **Encryption enabled on <date>.** Historical backups (if any) of your config database are still plaintext. If you have cloud backups, snapshots, or any copies of `/config` older than <date>, rotate the following credentials now:
> - Usenet provider passwords (on the provider's website)
> - Radarr/Sonarr API keys (Settings → General → Security → API Key → Generate New)
> - The NZBDAV SABnzbd API key (Settings → SABnzbd → Regenerate)
>
> [I've rotated my credentials — dismiss]

The dismiss button posts to a small endpoint that writes:

```
ConfigName = "encryption.post-migration-acknowledged"
ConfigValue = <ISO8601 UTC timestamp>
IsEncrypted = false
```

The banner stops rendering when `post-migration-acknowledged` exists. Neither row is encrypted — they're operational metadata, not secrets. If the operator never clicks dismiss, the banner stays visible forever; that's the desired behavior.

**Why two separate banners?** The plaintext-warning banner and the post-migration banner warn about completely different things:
- Plaintext-warning: "your *current* secrets are unencrypted — turn encryption on"
- Post-migration: "your *historical backups* are unencrypted — rotate credentials"

A user who opts in mid-upgrade should see the post-migration banner *after* the plaintext-warning banner goes away. Conflating them would let one warning suppress the other and miss the backup-rotation point entirely.

The `/api/encryption-status` endpoint is extended to return both signals:

```json
{
  "keySet": true,
  "plaintextSecretsCount": 0,
  "bannerSeverity": "none",
  "migrationCompletedAt": "2026-04-06T12:00:00Z",
  "postMigrationAcknowledged": false
}
```

The UI renders the plaintext-warning banner when `keySet === false`, and renders the post-migration banner when `migrationCompletedAt !== null && !postMigrationAcknowledged`.
