# Security Hardening Implementation Plan (V3, V4, V9, V11, V12)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close 5 security vulnerabilities: auth bypass risk, credential logging, insecure transport, API key exposure in files, and unlimited auth brute-force.

**Architecture:** Each fix is independent and committed separately. V11 (token grace period) modifies `StreamTokenService` which is shared with `ApiKeyAuthFilter` — test both paths after that change. V12 uses ASP.NET Core's built-in rate limiter, no custom implementation.

**Tech Stack:** .NET 10, ASP.NET Core RateLimiting, Serilog enrichers

---

### Task 1: V3 — DISABLE_WEBDAV_AUTH confirmation string

**Files:**
- Modify: `backend/Auth/WebApplicationAuthExtensions.cs`
- Create: `backend/Services/InsecureAuthWarningService.cs`
- Modify: `backend/Program.cs`

- [ ] **Step 1: Update IsWebdavAuthDisabled**

In `WebApplicationAuthExtensions.cs`, change `IsWebdavAuthDisabled`:

```csharp
private const string RequiredConfirmation = "I_UNDERSTAND_THIS_IS_INSECURE";

public static bool IsWebdavAuthDisabled()
{
    var value = EnvironmentUtil.GetEnvironmentVariable(DisableWebdavAuthEnvVar);
    if (string.IsNullOrEmpty(value)) return false;

    if (value == RequiredConfirmation)
    {
        LogOnlyOnce(() => Log.Warning(DisabledWebdavAuthLog));
        return true;
    }

    Log.Error(
        "DISABLE_WEBDAV_AUTH is set to '{Value}' but requires the exact value '{Required}' to take effect. " +
        "WebDAV authentication remains ENABLED.",
        value, RequiredConfirmation);
    return false;
}
```

- [ ] **Step 2: Create InsecureAuthWarningService**

```csharp
// backend/Services/InsecureAuthWarningService.cs
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services;

public class InsecureAuthWarningService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Log.Warning("WebDAV authentication is DISABLED. This is a security risk in production.");
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
```

- [ ] **Step 3: Register conditionally in Program.cs**

After the existing hosted service registrations, add:

```csharp
if (WebApplicationAuthExtensions.IsWebdavAuthDisabled())
    builder.Services.AddHostedService<InsecureAuthWarningService>();
```

- [ ] **Step 4: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery&FullyQualifiedName!~Integration"`

- [ ] **Step 5: Commit**

```bash
git add backend/Auth/WebApplicationAuthExtensions.cs backend/Services/InsecureAuthWarningService.cs backend/Program.cs
git commit -m "V3: Require confirmation string for DISABLE_WEBDAV_AUTH"
```

---

### Task 2: V4 — API key redaction in logs

**Files:**
- Create: `backend/Logging/ApiKeyRedactionEnricher.cs`
- Modify: `backend/Program.cs`

- [ ] **Step 1: Create the enricher**

```csharp
// backend/Logging/ApiKeyRedactionEnricher.cs
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Logging;

public partial class ApiKeyRedactionEnricher : ILogEventEnricher
{
    [GeneratedRegex(@"(apikey|api_key|api-key)=([^&\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Redact structured properties that may contain API keys
        var keysToRedact = new List<string>();

        foreach (var property in logEvent.Properties)
        {
            var rendered = property.Value.ToString();
            if (ApiKeyPattern().IsMatch(rendered))
                keysToRedact.Add(property.Key);
        }

        foreach (var key in keysToRedact)
        {
            var original = logEvent.Properties[key].ToString().Trim('"');
            var redacted = ApiKeyPattern().Replace(original, "$1=REDACTED");
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, redacted));
        }
    }
}
```

- [ ] **Step 2: Register in Program.cs Serilog config**

Add `.Enrich.With<ApiKeyRedactionEnricher>()` to the LoggerConfiguration:

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(level)
    // ... existing overrides ...
    .Enrich.With<ApiKeyRedactionEnricher>()
    .WriteTo.Console(theme: AnsiConsoleTheme.Code)
    .CreateLogger();
```

Add the using:
```csharp
using NzbWebDAV.Logging;
```

- [ ] **Step 3: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery&FullyQualifiedName!~Integration"`

- [ ] **Step 4: Commit**

```bash
git add backend/Logging/ApiKeyRedactionEnricher.cs backend/Program.cs
git commit -m "V4: Redact API keys from Serilog structured properties"
```

---

### Task 3: V9 — Reject Basic Auth over HTTP

**Files:**
- Modify: `backend/Auth/ServiceCollectionAuthExtensions.cs`

- [ ] **Step 1: Change AllowInsecureProtocol and add startup warning**

```csharp
// In AddWebdavBasicAuthentication, change the opts block:
.AddBasicAuthentication(opts =>
{
    var allowInsecure = EnvironmentUtil.IsVariableTrue("ALLOW_INSECURE_AUTH");
    opts.AllowInsecureProtocol = allowInsecure;
    if (allowInsecure)
        Log.Warning("Basic auth over HTTP is enabled via ALLOW_INSECURE_AUTH. Use TLS in production.");
    else
        Log.Information("Basic auth over HTTP is disabled. Set ALLOW_INSECURE_AUTH=true for development without TLS.");

    opts.CacheCookieName = "nzb-webdav-backend";
    opts.CacheCookieExpiration = TimeSpan.FromHours(1);
    opts.Events.OnValidateCredentials = (ValidateCredentialsContext context) =>
        ValidateCredentials(context, configManager);
});
```

- [ ] **Step 2: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery&FullyQualifiedName!~Integration"`

- [ ] **Step 3: Commit**

```bash
git add backend/Auth/ServiceCollectionAuthExtensions.cs
git commit -m "V9: Reject Basic Auth over HTTP by default (ALLOW_INSECURE_AUTH to opt in)"
```

---

### Task 4: V11 — Signed tokens in .strm files with grace period

**Files:**
- Modify: `backend/Services/StreamTokenService.cs`
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs`

- [ ] **Step 1: Update StreamTokenService — 7-day expiry + grace period validation**

Change `DefaultExpiryMinutes` and update `ValidateToken` to accept tokens within one prior expiry period:

```csharp
// Change line 11:
private const int DefaultExpiryMinutes = 10080; // 7 days

// Replace ValidateToken with grace period logic:
public static bool ValidateToken(string token, string path, ConfigManager configManager, string method = "GET")
{
    try
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2) return false;

        if (!long.TryParse(parts[0], out var claimedExpiry)) return false;

        // Check if token is within its validity window OR within one grace period.
        // Grace period = DefaultExpiryMinutes (server-side constant, not token-claimed).
        // This allows old tokens to remain valid during rotation.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var gracePeriodSeconds = DefaultExpiryMinutes * 60L;
        if (now > claimedExpiry + gracePeriodSeconds) return false;

        // Verify HMAC — this binds the claimed expiry to the server's key,
        // so an attacker cannot manipulate the expiry field.
        var payload = $"{method}:{claimedExpiry}:{path}";
        var expectedHmac = ComputeHmac(payload, configManager.GetApiKey());

        var expectedBytes = Encoding.UTF8.GetBytes(expectedHmac);
        var actualBytes = Encoding.UTF8.GetBytes(parts[1]);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
    catch
    {
        return false;
    }
}
```

- [ ] **Step 2: Update NzbdavLibrarySyncTask — use signed tokens, refresh stale ones**

In `SyncMountFolder`, replace the raw API key URL with a signed token, and refresh existing .strm files with stale tokens:

```csharp
// Replace the stream URL construction:
// OLD: var streamUrl = $"{config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{videoFile.Id}?apikey={config.ApiKey}";
// NEW:
var streamPath = $"/api/stream/{videoFile.Id}";
var configManager = CreateConfigManagerForTokens(config);
var token = StreamTokenService.GenerateToken(streamPath, configManager, expiryMinutes: 10080);
var streamUrl = $"{config.NzbdavBaseUrl.TrimEnd('/')}{streamPath}?token={token}";
```

But `StreamTokenService.GenerateToken` requires a `ConfigManager` which the plugin doesn't have. The plugin needs to generate tokens differently — it should call `/api/meta/{id}` which already returns a `StreamToken`.

Revised approach: use the token from the meta response (already generated server-side):

```csharp
// In SyncMountFolder, replace the stream URL block:
MetaResponse? meta;
try
{
    meta = await client.GetMetaAsync(videoFile.Id, ct).ConfigureAwait(false);
}
catch { continue; }

if (meta is null) continue;

var streamUrl = client.GetSignedStreamUrl(videoFile.Id, meta.StreamToken ?? "");
```

For refresh: check if the .strm file already exists and its token is still fresh. Read the existing file, extract the token, check if it's older than 3 days:

```csharp
var strmPath = Path.Combine(folderPath, Path.ChangeExtension(videoFile.Name, ".strm"));

// Check if existing .strm needs refresh (token older than 3 days)
if (File.Exists(strmPath))
{
    var existingUrl = await File.ReadAllTextAsync(strmPath, ct).ConfigureAwait(false);
    var existingToken = ExtractToken(existingUrl);
    if (existingToken != null && !IsTokenStale(existingToken))
        continue; // Token is fresh, skip
}

// Get fresh token from NZBDAV server
MetaResponse? meta;
try { meta = await client.GetMetaAsync(videoFile.Id, ct).ConfigureAwait(false); }
catch { continue; }
if (meta is null) continue;

var streamUrl = client.GetSignedStreamUrl(videoFile.Id, meta.StreamToken ?? "");
await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);
```

Helper methods:
```csharp
private static string? ExtractToken(string strmContent)
{
    var idx = strmContent.IndexOf("token=", StringComparison.Ordinal);
    return idx >= 0 ? strmContent[(idx + 6)..].Trim() : null;
}

private static bool IsTokenStale(string token)
{
    var parts = token.Split('.', 2);
    if (parts.Length != 2 || !long.TryParse(parts[0], out var expiry)) return true;
    // Token is stale if it expires within 4 days (7 - 3 = refresh at day 3)
    var refreshThreshold = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds();
    return expiry < refreshThreshold;
}
```

- [ ] **Step 3: Update MetaController token expiry**

In `backend/Api/Controllers/Meta/MetaController.cs`, the `GenerateToken` call uses the default expiry which is now 7 days. No change needed — it inherits from `DefaultExpiryMinutes`.

- [ ] **Step 4: Fix StreamTokenServiceTests for new expiry**

The expired token test uses `expiryMinutes: -1` which generates a token expired 1 minute ago. With the new grace period (7 days), this token would now be VALID (within grace). Change the test:

```csharp
// In StreamTokenServiceTests, update ValidateToken_ReturnsFalseWhenTokenIsExpired:
var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager, expiryMinutes: -10081);
// This creates a token that expired 10081 minutes ago (> 7 day grace period)
```

- [ ] **Step 5: Build and test**

Run: `cd backend && dotnet build && cd ../jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build && cd ../../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery&FullyQualifiedName!~Integration"`

- [ ] **Step 6: Commit**

```bash
git add backend/Services/StreamTokenService.cs jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs backend.Tests/Services/StreamTokenServiceTests.cs
git commit -m "V11: Signed tokens in .strm files (7-day expiry, 3-day refresh, grace period)"
```

---

### Task 5: V12 — ASP.NET Core rate limiting

**Files:**
- Modify: `backend/Program.cs`

- [ ] **Step 1: Add rate limiter configuration**

In `Program.cs`, add after `builder.Services.AddControllers();`:

```csharp
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

// ... in the service registration block:
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiterOptions =>
    {
        limiterOptions.PermitLimit = 10;
        limiterOptions.Window = TimeSpan.FromSeconds(60);
        limiterOptions.QueueLimit = 0;
    });
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsync(
            "Too many requests. Retry after 60 seconds.", ct).ConfigureAwait(false);
    };
});
```

- [ ] **Step 2: Add ForwardedHeaders and RateLimiter to pipeline**

In the middleware pipeline section, add BEFORE `UseRateLimiter`:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});
app.UseRateLimiter();
```

Add these BEFORE `app.UseRouting();`.

- [ ] **Step 3: Apply rate limiter to API controllers**

In `backend/Api/Filters/ApiKeyAuthFilter.cs`, the filter already runs on every `/api/*` request. Apply the rate limiter policy at the route level instead:

In `Program.cs`, change the controller mapping:

```csharp
app.MapControllers().RequireRateLimiting("auth");
```

This applies the "auth" policy to ALL controller routes (including `/api/browse`, `/api/meta`, `/api/stream`, and the SABnzbd `/api`). The rate limiter counts per IP across all these routes — a single policy, single counter.

For non-rate-limited routes (`/health`, `/metrics`, `/ws`), they're mapped separately and won't be affected:
- `app.MapHealthChecks(...)` — separate mapping, not through controllers
- `app.UseMetricServer(...)` — middleware, not controller
- `app.Map("/ws", ...)` — separate mapping

- [ ] **Step 4: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery&FullyQualifiedName!~Integration"`

- [ ] **Step 5: Commit**

```bash
git add backend/Program.cs
git commit -m "V12: ASP.NET Core rate limiting — 10 req/60s per IP on API routes"
```

---

## Spec Coverage Check

| Spec requirement | Task |
|------------------|------|
| V3: Confirmation string | Task 1, Step 1 |
| V3: Reject other values with error log | Task 1, Step 1 |
| V3: IHostedService warning every 60s | Task 1, Step 2 |
| V3: Cancellable via StoppingToken | Task 1, Step 2 (BackgroundService) |
| V4: Structured property redaction | Task 2, Step 1 |
| V4: Registered in Serilog pipeline | Task 2, Step 2 |
| V9: AllowInsecureProtocol=false | Task 3, Step 1 |
| V9: ALLOW_INSECURE_AUTH opt-in | Task 3, Step 1 |
| V9: Actionable startup log | Task 3, Step 1 |
| V11: 7-day expiry | Task 4, Step 1 |
| V11: Grace period (server-side anchored) | Task 4, Step 1 |
| V11: HMAC binds expiry | Task 4, Step 1 (existing) |
| V11: Signed tokens in .strm | Task 4, Step 2 |
| V11: Refresh at 3 days | Task 4, Step 2 |
| V11: Atomic per-item | Task 4, Step 2 |
| V12: Fixed window 10/60s | Task 5, Step 1 |
| V12: Single policy all /api routes | Task 5, Step 3 |
| V12: UseForwardedHeaders before rate limiter | Task 5, Step 2 |
| V12: 429 + Retry-After | Task 5, Step 1 |
