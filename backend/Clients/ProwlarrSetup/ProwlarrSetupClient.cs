using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NzbWebDAV.Clients.ProwlarrSetup;

public sealed class ProwlarrSetupOptions
{
    public ProwlarrSetupOptions(string sonarrUrl, string sonarrApiKey, string radarrUrl, string radarrApiKey,
        IReadOnlyCollection<ProwlarrNewznabIndexer> indexers, string? prowlarrUrl = null, bool forceSecretUpdate = false,
        Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    { SonarrUrl = sonarrUrl; SonarrApiKey = sonarrApiKey; RadarrUrl = radarrUrl; RadarrApiKey = radarrApiKey; Indexers = indexers; ProwlarrUrl = prowlarrUrl; ForceSecretUpdate = forceSecretUpdate; AssertMutationLeaseAsync = assertMutationLeaseAsync; }
    public string SonarrUrl { get; }
    public string SonarrApiKey { get; }
    public string RadarrUrl { get; }
    public string RadarrApiKey { get; }
    public IReadOnlyCollection<ProwlarrNewznabIndexer> Indexers { get; }
    public string? ProwlarrUrl { get; }
    /// <summary>A masked secret is retained on ordinary reconciliation. When true only the resource containing it is PUT.</summary>
    public bool ForceSecretUpdate { get; }
    public Func<CancellationToken, Task>? AssertMutationLeaseAsync { get; }
    public override string ToString() => $"ProwlarrSetupOptions(Indexers={Indexers?.Count ?? 0})";
}

public sealed class ProwlarrNewznabIndexer
{
    public ProwlarrNewznabIndexer(string name, string baseUrl, string apiKey, IReadOnlyDictionary<string, object?>? fields = null, int? appProfileId = null, bool allowPrivateNetwork = false)
    { Name = name; BaseUrl = baseUrl; ApiKey = apiKey; Fields = fields; AppProfileId = appProfileId; AllowPrivateNetwork = allowPrivateNetwork; }
    public string Name { get; }
    public string BaseUrl { get; }
    public string ApiKey { get; }
    public IReadOnlyDictionary<string, object?>? Fields { get; }
    public int? AppProfileId { get; }
    public bool AllowPrivateNetwork { get; }
    public override string ToString() => $"ProwlarrNewznabIndexer(Name={Name}, Fields={Fields?.Count ?? 0})";
}

public sealed class ProwlarrSetupResult(int created, int updated, int unchanged)
{
    public int Created { get; } = created;
    public int Updated { get; } = updated;
    public int Unchanged { get; } = unchanged;
    public static ProwlarrSetupResult Empty { get; } = new(0, 0, 0);
    public override string ToString() => $"ProwlarrSetupResult(Created={Created}, Updated={Updated}, Unchanged={Unchanged})";
}
public sealed class ProwlarrSetupConflictException(string message) : InvalidOperationException(message);
public sealed class ProwlarrSetupProtocolException(string message) : InvalidOperationException(message);
public sealed class ProwlarrSetupTransportException : HttpRequestException { public ProwlarrSetupTransportException() : base("Prowlarr TransportError.") { } }

/// <summary>Small, sequential reconciler for Prowlarr 2.5.2.5491's v1 resources.</summary>
public sealed class ProwlarrSetupClient : IDisposable
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const string Prefix = "api/v1";
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxOutboundPayloadBytes = 8 * 1024 * 1024;
    private const int MaxDepth = 32, MaxProperties = 512, MaxArrayItems = 8192, MaxElements = 250_000, MaxStringChars = 64 * 1024;
    private const int MaxIndexers = 100;
    // Prowlarr's pinned schemas are far below this; retain room for vendor fields.
    private const int MaxIndexerFields = 64;
    // Aggregate UTF-8 budget for caller-supplied indexer field names and values.
    private const int MaxInputBytes = 1024 * 1024;
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30), MaxTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonDocument NullDocument = JsonDocument.Parse("null");
    private static readonly byte[] MaskedSecret = Encoding.UTF8.GetBytes("********");
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsHttp;

    public ProwlarrSetupClient(Uri prowlarrUrl, string apiKey, TimeSpan? timeout = null) : this(SetupHttpClientFactory.Create(), true, prowlarrUrl, apiKey, timeout) { }
    public ProwlarrSetupClient(string prowlarrUrl, string apiKey, TimeSpan? timeout = null) : this(new Uri(prowlarrUrl, UriKind.Absolute), apiKey, timeout) { }
    // Internal solely for deterministic loopback tests. Production has no handler seam.
    internal ProwlarrSetupClient(HttpMessageHandler handler, Uri prowlarrUrl, string apiKey, TimeSpan? timeout = null) : this(new HttpClient(handler, true), true, prowlarrUrl, apiKey, timeout) { }
    internal ProwlarrSetupClient(HttpMessageHandler handler, string prowlarrUrl, string apiKey, TimeSpan? timeout = null) : this(handler, new Uri(prowlarrUrl, UriKind.Absolute), apiKey, timeout) { }

    private ProwlarrSetupClient(HttpClient http, bool ownsHttp, Uri url, string apiKey, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(url);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An API key is required.", nameof(apiKey));
        _baseUri = CanonicalBase(url, nameof(url));
        _apiKey = apiKey; _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > MaxTimeout) throw new ArgumentOutOfRangeException(nameof(timeout));
        _http = http; _ownsHttp = ownsHttp;
    }

    /// <summary>Verifies the exact managed applications and requested indexers.</summary>
    public async Task<bool> VerifyManagedResourcesAsync(
        ProwlarrSetupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var token = timeout.Token;
        var snapshot = Capture(options, token);
        try
        {
            Validate(snapshot);
            using var indexerSchemaDoc = await GetDocumentAsync("indexer/schema", token, cancellationToken).ConfigureAwait(false);
            var indexerSchema = SelectSchema(indexerSchemaDoc.Document.RootElement, "Newznab", "NewznabSettings", "Generic Newznab", true, token);
            var profile = snapshot.Indexers.Count == 0
                ? 0
                : await SelectProfileAsync(token, cancellationToken, snapshot.Indexers).ConfigureAwait(false);
            using var applicationsSchemaDoc = await GetDocumentAsync("applications/schema", token, cancellationToken).ConfigureAwait(false);
            var sonarrSchema = SelectSchema(applicationsSchemaDoc.Document.RootElement, "Sonarr", "SonarrSettings", null, false, token);
            var radarrSchema = SelectSchema(applicationsSchemaDoc.Document.RootElement, "Radarr", "RadarrSettings", null, false, token);
            using var applications = await GetDocumentAsync("applications", token, cancellationToken).ConfigureAwait(false);
            using var indexers = await GetDocumentAsync("indexer", token, cancellationToken).ConfigureAwait(false);

            foreach (var indexer in snapshot.Indexers)
            {
                var existing = FindResource(indexers.Document.RootElement, indexer.Name, "indexer", indexer, null, token);
                if (existing is null)
                    return false;
                // A masked field cannot fingerprint the caller's supplied
                // secret. Build the test payload with the exact input so a
                // stale rotated key cannot be reported Ready merely because
                // the upstream returned "********".
                using var desired = BuildPayload(indexerSchema, existing, indexer, null, profile, true, token);
                var indexerId = ResourceId(existing);
                // A GET masks secrets and therefore cannot prove which
                // credential is persisted. First compare the non-secret
                // fingerprint, then ask Prowlarr to test this saved id via
                // test-all. Never substitute the caller payload for this.
                if (!Equivalent(existing, desired, indexerSchema, false)
                    || !await TestPersistedResourceAsync("indexer", indexerId, token, cancellationToken).ConfigureAwait(false))
                    return false;
            }

            var prowlarrUrl = snapshot.ProwlarrUrl ?? _baseUri.ToString();
            var sonarr = new App("NZBDAV Sonarr", "Sonarr", snapshot.SonarrUrl, snapshot.SonarrApiKey, sonarrSchema, prowlarrUrl);
            var radarr = new App("NZBDAV Radarr", "Radarr", snapshot.RadarrUrl, snapshot.RadarrApiKey, radarrSchema, prowlarrUrl);
            foreach (var app in new[] { sonarr, radarr })
            {
                var existing = FindResource(applications.Document.RootElement, app.Name, "applications", null, app, token);
                if (existing is null)
                    return false;
                using var desired = BuildPayload(app.Schema, existing, null, app, 0, true, token);
                var applicationId = ResourceId(existing);
                if (!Equivalent(existing, desired, app.Schema, false)
                    || !await TestPersistedResourceAsync("applications", applicationId, token, cancellationToken).ConfigureAwait(false))
                    return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ProwlarrSetupTransportException)
        {
            return false;
        }
        catch (ProwlarrSetupHttpException)
        {
            return false;
        }
        catch (ProwlarrSetupProtocolException)
        {
            return false;
        }
        finally
        {
            snapshot.Clear();
        }
    }

    public async Task<ProwlarrSetupResult> SetupAsync(
        ProwlarrSetupOptions options,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout); // includes snapshotting and waiting for the process-wide gate.
        try { return await SetupCoreAsync(options, timeout.Token, cancellationToken, assertMutationLeaseAsync).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ProwlarrSetupTransportException(); }
    }

    private async Task<ProwlarrSetupResult> SetupCoreAsync(ProwlarrSetupOptions source, CancellationToken token, CancellationToken callerToken, Func<CancellationToken, Task>? assertMutationLeaseAsync)
    {
        callerToken.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        var snapshot = Capture(source, token, assertMutationLeaseAsync); // before any await: caller collections are not aliases.
        try
        {
            Validate(snapshot);
            token.ThrowIfCancellationRequested();
            await SetupGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                CurrentForceSecretUpdate = snapshot.Force;
                var result = new Counts();
                // Keep the large indexer catalog alive only while its typed schema is in use.
                using (var indexerSchemaDoc = await GetDocumentAsync("indexer/schema", token, callerToken).ConfigureAwait(false))
                {
                    var indexerSchema = SelectSchema(indexerSchemaDoc.Document.RootElement, "Newznab", "NewznabSettings", "Generic Newznab", true, token);
                    var profile = snapshot.Indexers.Count == 0 ? 0 : await SelectProfileAsync(token, callerToken, snapshot.Indexers).ConfigureAwait(false);
                    // Every target is discovered immediately before its decision and mutation. No stale plan survives an await.
                    foreach (var indexer in snapshot.Indexers)
                    {
                        token.ThrowIfCancellationRequested();
                        result.Add(await ReconcileAsync("indexer", indexer.Name, indexer, indexerSchema, profile, null, snapshot.AssertMutationLeaseAsync, token, callerToken).ConfigureAwait(false));
                    }
                }
                using var applicationSchemaDoc = await GetDocumentAsync("applications/schema", token, callerToken).ConfigureAwait(false);
                var sonarrSchema = SelectSchema(applicationSchemaDoc.Document.RootElement, "Sonarr", "SonarrSettings", null, false, token);
                var radarrSchema = SelectSchema(applicationSchemaDoc.Document.RootElement, "Radarr", "RadarrSettings", null, false, token);
                var applications = new[]
                {
                    new App("NZBDAV Sonarr", "Sonarr", snapshot.SonarrUrl, snapshot.SonarrApiKey, sonarrSchema),
                    new App("NZBDAV Radarr", "Radarr", snapshot.RadarrUrl, snapshot.RadarrApiKey, radarrSchema)
                };
                var prowlarrUrl = snapshot.ProwlarrUrl ?? _baseUri.ToString();
                foreach (var app in applications)
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(await ReconcileAsync("applications", app.Name, null, app.Schema, 0, app with { ProwlarrUrl = prowlarrUrl }, snapshot.AssertMutationLeaseAsync, token, callerToken).ConfigureAwait(false));
                }
                return new ProwlarrSetupResult(result.Created, result.Updated, result.Unchanged);
            }
            finally { CurrentForceSecretUpdate = false; SetupGate.Release(); }
        }
        finally { snapshot.Clear(); }
    }

    private async Task<Outcome> ReconcileAsync(string resource, string name, IndexerSnapshot? indexer, Schema schema, int profile, App? app, Func<CancellationToken, Task>? assertMutationLeaseAsync, CancellationToken token, CancellationToken callerToken)
    {
        using var collection = await GetDocumentAsync(resource, token, callerToken).ConfigureAwait(false);
        var existing = FindResource(collection.Document.RootElement, name, resource, indexer, app, token);
        // The list response is only an identity hint. Re-read an existing resource immediately
        // before deciding so an external edit between the first and final GET is not overwritten
        // with stale metadata or secrets.
        using var finalCollection = existing is not null ? await GetDocumentAsync(resource, token, callerToken).ConfigureAwait(false) : null;
        if (finalCollection is not null)
            existing = FindResource(finalCollection.Document.RootElement, name, resource, indexer, app, token);
        if (indexer is not null)
            foreach (var field in indexer.Fields.Keys)
                if (!schema.Names.Contains(field, StringComparer.OrdinalIgnoreCase)) throw new ProwlarrSetupProtocolException("Pinned Generic Newznab does not contain the requested field.");
        var payload = BuildPayload(schema, existing, indexer, app, profile, _forceSecretUpdate: CurrentForceSecretUpdate, token);
        try
        {
            if (existing is not null && Equivalent(existing, payload, schema, CurrentForceSecretUpdate)) return Outcome.Unchanged;
            var id = existing is not null ? ResourceId(existing) : 0;
            try
            {
                await SendJsonAsync(id == 0 ? HttpMethod.Post : HttpMethod.Put, id == 0 ? resource : $"{resource}/{id}", payload, token, callerToken, assertMutationLeaseAsync).ConfigureAwait(false);
                return id == 0 ? Outcome.Created : Outcome.Updated;
            }
            catch (ProwlarrSetupHttpException ex) when (id == 0 && ex.StatusCode == HttpStatusCode.Conflict)
            {
                // One bounded retry: re-read, revalidate identity, then PUT or settle unchanged.
                using var after = await GetDocumentAsync(resource, token, callerToken).ConfigureAwait(false);
                var winner = FindResource(after.Document.RootElement, name, resource, indexer, app, token);
                if (winner is null) throw new ProwlarrSetupConflictException("Prowlarr rejected a new resource because its identity is already in use.");
                var retry = BuildPayload(schema, winner, indexer, app, profile, CurrentForceSecretUpdate, token);
                try
                {
                    if (Equivalent(winner, retry, schema, CurrentForceSecretUpdate)) return Outcome.Unchanged;
                    await SendJsonAsync(HttpMethod.Put, $"{resource}/{ResourceId(winner)}", retry, token, callerToken, assertMutationLeaseAsync).ConfigureAwait(false);
                    return Outcome.Updated;
                }
                finally { retry.Dispose(); }
            }
        }
        finally { payload.Dispose(); }
    }

    // Kept as a property only to make the per-run snapshot explicit and immutable to requests.
    private bool CurrentForceSecretUpdate { get; set; }

    private async Task<int> SelectProfileAsync(CancellationToken token, CancellationToken callerToken, IReadOnlyList<IndexerSnapshot> indexers)
    {
        using var doc = await GetDocumentAsync("appprofile", token, callerToken).ConfigureAwait(false);
        var ids = new HashSet<int>(); var profiles = new List<(int Id, string Name)>();
        foreach (var p in RequireArray(doc.Document.RootElement, "appprofile", token).EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || number <= 0 || !p.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            if (!ids.Add(number)) throw new ProwlarrSetupProtocolException("Prowlarr application profiles have ambiguous ids.");
            profiles.Add((number, n.GetString()!.Trim()));
        }
        var requested = indexers.Select(i => i.AppProfileId).Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToArray();
        if (requested.Length > 1) throw new ProwlarrSetupConflictException("Requested Prowlarr application profiles are inconsistent.");
        if (requested.Length == 1) { if (profiles.Count(p => p.Id == requested[0]) != 1) throw new ProwlarrSetupProtocolException("Requested Prowlarr application profile is unavailable."); return requested[0]; }
        if (profiles.Count == 0) throw new ProwlarrSetupProtocolException("Prowlarr has no valid application profiles.");
        var standard = profiles.Where(p => p.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (standard.Length > 1) throw new ProwlarrSetupProtocolException("Prowlarr application profile identity is ambiguous.");
        return standard.Length == 1 ? standard[0].Id : profiles.Min(p => p.Id);
    }

    private OwnedPayload BuildPayload(Schema schema, Resource? existing, IndexerSnapshot? indexer, App? app, int profile, bool _forceSecretUpdate, CancellationToken token)
    {
        var writer = new OwnedPayload(MaxOutboundPayloadBytes);
        try
        {
        using (var json = new Utf8JsonWriter(writer))
        {
            json.WriteStartObject();
            if (existing is not null)
                foreach (var property in existing.Element.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsManagedTopLevel(property.Name, indexer is not null) && !property.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) { json.WritePropertyName(property.Name); property.Value.WriteTo(json); }
                }
            else
                foreach (var schemaProperty in schema.Source.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsManagedTopLevel(schemaProperty.Name, indexer is not null) && !schemaProperty.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) { json.WritePropertyName(schemaProperty.Name); schemaProperty.Value.WriteTo(json); }
                }
            json.WriteString("name", indexer?.Name ?? app!.Name);
            WriteOrDefault(json, "implementation", schema.Implementation);
            WriteOrDefault(json, "implementationName", schema.ImplementationName);
            WriteOrDefault(json, "configContract", schema.Contract);
            if (indexer is not null) { json.WriteNumber("appProfileId", profile); json.WriteBoolean("enable", true); }
            else json.WriteString("syncLevel", "fullSync");
            json.WritePropertyName("fields"); json.WriteStartArray();
            var old = existing is not null ? Fields(existing.Element) : new(StringComparer.OrdinalIgnoreCase);
            foreach (var field in schema.Fields)
            {
                token.ThrowIfCancellationRequested();
                json.WriteStartObject();
                foreach (var metadata in field.Source.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!metadata.NameEquals("name") && !metadata.NameEquals("value")) { json.WritePropertyName(metadata.Name); metadata.Value.WriteTo(json); }
                }
                json.WriteString("name", field.Name); json.WritePropertyName("value");
                if (field.IsSecret && old.TryGetValue(field.Name, out var masked) && masked.IsMasked && !_forceSecretUpdate) masked.Value.WriteTo(json);
                else if (WriteRequested(json, field.Name, indexer, app)) { }
                else if (old.TryGetValue(field.Name, out var previous) && previous.HasValue) previous.Value.WriteTo(json);
                else field.Default.WriteTo(json);
                json.WriteEndObject();
            }
            foreach (var oldField in old.Values)
            {
                token.ThrowIfCancellationRequested();
                if (!schema.Names.Contains(oldField.Name, StringComparer.OrdinalIgnoreCase)) oldField.Source.WriteTo(json);
            }
            json.WriteEndArray(); json.WriteEndObject(); json.Flush();
        }
        return writer;
        }
        catch { writer.Dispose(); throw; }

        static void WriteOrDefault(Utf8JsonWriter w, string name, string value) { w.WriteString(name, value); }
    }

    private static bool IsManagedTopLevel(string name, bool indexer) => name.Equals("name", StringComparison.OrdinalIgnoreCase) || name.Equals("implementation", StringComparison.OrdinalIgnoreCase) || name.Equals("implementationName", StringComparison.OrdinalIgnoreCase) || name.Equals("configContract", StringComparison.OrdinalIgnoreCase) || name.Equals("fields", StringComparison.OrdinalIgnoreCase) || (indexer ? name.Equals("appProfileId", StringComparison.OrdinalIgnoreCase) || name.Equals("enable", StringComparison.OrdinalIgnoreCase) : name.Equals("syncLevel", StringComparison.OrdinalIgnoreCase));
    private static bool WriteRequested(Utf8JsonWriter writer, string name, IndexerSnapshot? i, App? a)
    {
        if (i is not null && name.Equals("baseUrl", StringComparison.OrdinalIgnoreCase)) { writer.WriteStringValue(i.BaseUrl); return true; }
        if (i is not null && name.Equals("apiKey", StringComparison.OrdinalIgnoreCase)) { writer.WriteStringValue(i.ApiKey); return true; }
        if (i is not null && i.Fields.TryGetValue(name, out var value)) { value.WriteTo(writer); return true; }
        if (a is not null && name.Equals("prowlarrUrl", StringComparison.OrdinalIgnoreCase)) { writer.WriteStringValue(a.ProwlarrUrl); return true; }
        if (a is not null && name.Equals("baseUrl", StringComparison.OrdinalIgnoreCase)) { writer.WriteStringValue(a.Url); return true; }
        if (a is not null && name.Equals("apiKey", StringComparison.OrdinalIgnoreCase)) { writer.WriteStringValue(a.ApiKey); return true; }
        return false;
    }

    private static bool Equivalent(Resource existing, OwnedPayload payload, Schema schema, bool force)
    {
        using var doc = JsonDocument.Parse(payload.Memory); var wanted = doc.RootElement;
        foreach (var top in new[] { "name", "implementation", "implementationName", "configContract", "appProfileId", "enable", "syncLevel" })
            if (wanted.TryGetProperty(top, out var w) && (!existing.Element.TryGetProperty(top, out var e) || !JsonElement.DeepEquals(e, w))) return false;
        var old = Fields(existing.Element); var desired = Fields(wanted);
        foreach (var field in schema.Fields)
        {
            if (!old.TryGetValue(field.Name, out var o) || !desired.TryGetValue(field.Name, out var d) || !d.HasValue) return false;
            // Prowlarr omits value for persisted default fields. That is only
            // equivalent when the desired payload retains the pinned schema
            // default; caller-supplied non-default values still fail closed.
            if (!o.HasValue)
            {
                if (!JsonElement.DeepEquals(d.Value, field.Default)) return false;
                continue;
            }
            if (field.IsSecret && o.IsMasked && !force) continue;
            if (!JsonElement.DeepEquals(o.Value, d.Value)) return false;
        }
        return true;
    }

    private static Dictionary<string, Field> Fields(JsonElement resource)
    {
        if (!resource.TryGetProperty("fields", out var array) || array.ValueKind != JsonValueKind.Array) throw new ProwlarrSetupProtocolException("Prowlarr resource fields are invalid.");
        var fields = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) throw new ProwlarrSetupProtocolException("Prowlarr resource fields are invalid.");
            var n = name.GetString()!.Trim(); var has = value.TryGetProperty("value", out var v); if (!fields.TryAdd(n, new Field(n, value, has, v))) throw new ProwlarrSetupProtocolException("Prowlarr resource contains duplicate fields.");
        }
        return fields;
    }

    private static Resource? FindResource(JsonElement root, string name, string resource, IndexerSnapshot? indexer, App? app, CancellationToken token)
    {
        var found = new List<Resource>();
        foreach (var item in RequireArray(root, resource, token).EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) throw new ProwlarrSetupProtocolException($"Prowlarr {resource} resource list is invalid.");
            if (string.Equals(n.GetString()!.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) found.Add(new Resource(item));
        }
        if (found.Count > 1) throw new ProwlarrSetupConflictException($"Prowlarr {resource} names are ambiguous.");
        if (found.Count == 1)
        {
            var fields = Fields(found[0].Element);
            if (indexer is not null && (!Implementation(found[0].Element, "Newznab") || !fields.TryGetValue("baseUrl", out var indexerBase) || !SameUriValue(indexerBase.Value, indexer.BaseUrl))) throw new ProwlarrSetupConflictException("Prowlarr indexer name is already used by another resource.");
            if (app is not null && (!Implementation(found[0].Element, app.Implementation) || !fields.TryGetValue("baseUrl", out var appBase) || !SameUriValue(appBase.Value, app.Url) || (fields.TryGetValue("prowlarrUrl", out var p) && p.HasValue && !SameUriValue(p.Value, app.ProwlarrUrl)))) throw new ProwlarrSetupConflictException("Prowlarr application name is already used by another resource.");
            return found[0];
        }
        return null;
    }
    private static int ResourceId(Resource resource) => resource.Element.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) && value > 0 ? value : throw new ProwlarrSetupProtocolException("Prowlarr resource has no usable id.");
    private static bool Implementation(JsonElement e, string value) => e.TryGetProperty("implementation", out var i) && i.ValueKind == JsonValueKind.String && i.GetString()!.Equals(value, StringComparison.OrdinalIgnoreCase);
    private static bool SameUriValue(JsonElement value, string wanted) => value.ValueKind == JsonValueKind.String && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var current) && Uri.TryCreate(wanted, UriKind.Absolute, out var desired) && SameUri(current, desired);

    private static Schema SelectSchema(JsonElement root, string implementation, string contract, string? name, bool generic, CancellationToken token)
    {
        var matches = new List<Schema>();
        foreach (var item in RequireArray(root, "schema", token).EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); if (item.ValueKind != JsonValueKind.Object) throw new ProwlarrSetupProtocolException("Prowlarr schema is invalid.");
            if (!item.TryGetProperty("implementation", out var impl) || impl.ValueKind != JsonValueKind.String || !impl.GetString()!.Equals(implementation, StringComparison.OrdinalIgnoreCase) || !item.TryGetProperty("configContract", out var c) || c.ValueKind != JsonValueKind.String || !c.GetString()!.Equals(contract, StringComparison.OrdinalIgnoreCase) || (name is not null && (!item.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || !n.GetString()!.Equals(name, StringComparison.OrdinalIgnoreCase)))) continue;
            matches.Add(ParseSchema(item, implementation, contract, generic));
        }
        return matches.Count switch { 1 => matches[0], 0 => throw new ProwlarrSetupProtocolException($"Prowlarr schema for {implementation} is unavailable."), _ => throw new ProwlarrSetupProtocolException($"Prowlarr schema for {implementation} is ambiguous.") };
    }
    private static Schema ParseSchema(JsonElement source, string implementation, string contract, bool generic)
    {
        var fields = new List<SchemaField>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!source.TryGetProperty("fields", out var array) || array.ValueKind != JsonValueKind.Array) throw new ProwlarrSetupProtocolException("Prowlarr schema fields are invalid.");
        var allowed = new[] { "baseUrl", "apiPath", "apiKey", "additionalParameters", "vipExpiration", "baseSettings.queryLimit", "baseSettings.grabLimit", "baseSettings.limitsUnit", "prowlarrUrl" };
        foreach (var field in array.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) throw new ProwlarrSetupProtocolException("Prowlarr schema fields are invalid.");
            var name = n.GetString()!.Trim(); if (!names.Add(name)) throw new ProwlarrSetupProtocolException("Prowlarr schema has duplicate fields.");
            if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var defaultValue = field.TryGetProperty("value", out var value) ? value : field.TryGetProperty("defaultValue", out var fallback) ? fallback : NullDocument.RootElement;
            fields.Add(new SchemaField(name, field, defaultValue, Credential(name)));
        }
        foreach (var required in generic ? new[] { "baseUrl", "apiPath", "apiKey", "additionalParameters", "vipExpiration", "baseSettings.queryLimit", "baseSettings.grabLimit", "baseSettings.limitsUnit" } : new[] { "prowlarrUrl", "baseUrl", "apiKey" })
            if (!names.Contains(required)) throw new ProwlarrSetupProtocolException("Pinned Prowlarr schema is missing a required field.");
        return new Schema(source, implementation, source.TryGetProperty("implementationName", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString()! : implementation, contract, fields);
    }

    private async Task<OwnedDocument> GetDocumentAsync(string path, CancellationToken token, CancellationToken callerToken)
    {
        using var exchange = await ExecuteAsync(HttpMethod.Get, path, null, token, callerToken).ConfigureAwait(false);
        using var response = exchange.Response; if (response.StatusCode != HttpStatusCode.OK) throw new ProwlarrSetupHttpException(path, response.StatusCode);
        return await ReadDocumentAsync(response, path, token, callerToken).ConfigureAwait(false);
    }
    private async Task SendJsonAsync(HttpMethod method, string path, OwnedPayload payload, CancellationToken token, CancellationToken callerToken, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        using var exchange = await ExecuteAsync(method, path, payload, token, callerToken, assertMutationLeaseAsync).ConfigureAwait(false);
        using var response = exchange.Response;
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted or HttpStatusCode.NoContent)) throw new ProwlarrSetupHttpException(path, response.StatusCode);
        if (response.StatusCode != HttpStatusCode.NoContent && response.Content.Headers.ContentLength is not 0) { using var body = await ReadDocumentAsync(response, path, token, callerToken).ConfigureAwait(false); }
    }

    /// <summary>
    /// Tests the persisted resources, rather than testing a caller-supplied
    /// copy of a resource. Prowlarr 2.5.2.5491 exposes this as the inherited
    /// provider test-all endpoint; its result includes the persisted resource
    /// id and validation outcome.
    /// </summary>
    private async Task<bool> TestPersistedResourceAsync(string resource, int id, CancellationToken token, CancellationToken callerToken)
    {
        // Prowlarr exposes persisted resource validation as POST, but testall is
        // read-only. Do not invoke the mutation lease callback for this probe.
        using var exchange = await ExecuteAsync(HttpMethod.Post, $"{resource}/testall", null, token, callerToken).ConfigureAwait(false);
        using var response = exchange.Response;
        if (response.StatusCode != HttpStatusCode.OK)
            return false;

        try
        {
            using var document = await ReadDocumentAsync(response, $"{resource}/testall", token, callerToken).ConfigureAwait(false);
            if (document.Document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var result in document.Document.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("id", out var resultIdValue)
                    || !resultIdValue.TryGetInt32(out var resultId)
                    || resultId != id)
                    continue;

                // ProviderTestAllResult.IsValid is serialized by the pinned
                // API as isValid. Treat a missing/non-boolean value as failure.
                return result.TryGetProperty("isValid", out var valid)
                    && valid.ValueKind == JsonValueKind.True;
            }
        }
        catch (ProwlarrSetupProtocolException)
        {
            return false;
        }

        return false;
    }

    private async Task<Exchange> ExecuteAsync(HttpMethod method, string path, OwnedPayload? payload, CancellationToken token, CancellationToken callerToken, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        using var request = new HttpRequestMessage(method, Endpoint(path)); request.Headers.TryAddWithoutValidation(ApiKeyHeader, _apiKey);
        if (payload is not null) request.Content = new SecretJsonContent(payload);
        var isMutation = assertMutationLeaseAsync is not null
            && (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Delete);
        if (isMutation)
            await assertMutationLeaseAsync!(token).ConfigureAwait(false);

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                response = null;
                throw new ProwlarrSetupTransportException();
            }
            return new Exchange(response);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new ProwlarrSetupTransportException(); }
        catch (ProwlarrSetupTransportException) { throw; }
        catch (HttpRequestException) { throw new ProwlarrSetupTransportException(); }
        finally
        {
            if (isMutation)
            {
                try
                {
                    // The post-write assertion is required even for a
                    // transport error or an acknowledgement lost after the
                    // server committed the mutation.
                    await assertMutationLeaseAsync!(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }
            }
        }
    }
    private static async Task<OwnedDocument> ReadDocumentAsync(HttpResponseMessage response, string path, CancellationToken token, CancellationToken callerToken)
    {
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) throw new ProwlarrSetupProtocolException($"Prowlarr response for /{Prefix}/{path} has an invalid media type.");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new ProwlarrSetupProtocolException("Prowlarr response is too large.");
        var buffer = ArrayPool<byte>.Shared.Rent(MaxResponseBytes); var count = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            while (count < MaxResponseBytes)
            {
                token.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(count, MaxResponseBytes - count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count == MaxResponseBytes)
            {
                var overflow = ArrayPool<byte>.Shared.Rent(1);
                try
                {
                    if (await stream.ReadAsync(overflow.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                        throw new ProwlarrSetupProtocolException("Prowlarr response is too large.");
                }
                finally { ArrayPool<byte>.Shared.Return(overflow, true); }
            }
            try { ValidateUtf8(buffer.AsSpan(0, count), token); }
            catch (JsonException) { throw new ProwlarrSetupProtocolException("Prowlarr returned invalid JSON."); }
            JsonDocument document;
            try { document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Disallow }); }
            catch (JsonException) { throw new ProwlarrSetupProtocolException("Prowlarr returned invalid JSON."); }
            return new OwnedDocument(document, buffer);
        }
        catch { ArrayPool<byte>.Shared.Return(buffer, true); throw; }
    }

    // Validate the wire representation before allocating a JsonDocument. In particular,
    // this keeps a many-tiny-token response from first materializing an unbounded DOM.
    private static void ValidateUtf8(ReadOnlySpan<byte> data, CancellationToken token)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaxDepth });
        var frames = new Frame[MaxDepth + 1];
        var depth = 0; var elements = 0; var roots = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (depth == 0 || !frames[depth - 1].Object || ++frames[depth - 1].Properties > MaxProperties || reader.ValueSpan.Length > MaxStringChars)
                    throw new ProwlarrSetupProtocolException("Prowlarr JSON contains invalid object properties.");
                var name = reader.GetString()!;
                if (!frames[depth - 1].Names!.Add(name)) throw new ProwlarrSetupProtocolException("Prowlarr JSON contains invalid object properties.");
                continue;
            }
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                if (depth == 0 && ++roots > 1) throw new JsonException();
                if (++elements > MaxElements || depth > MaxDepth) throw new ProwlarrSetupProtocolException("Prowlarr JSON exceeds safety bounds.");
                if (depth > 0 && !frames[depth - 1].Object) frames[depth - 1].Items++;
                if (reader.TokenType == JsonTokenType.StartArray && depth > 0 && frames[depth - 1].Items > MaxArrayItems) throw new ProwlarrSetupProtocolException("Prowlarr JSON contains too many array items.");
                frames[depth++] = new Frame(reader.TokenType == JsonTokenType.StartObject);
                continue;
            }
            if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) { if (depth-- <= 0) throw new JsonException(); continue; }
            if (reader.TokenType == JsonTokenType.None) throw new JsonException();
            if (++elements > MaxElements) throw new ProwlarrSetupProtocolException("Prowlarr JSON exceeds safety bounds.");
            if (depth > 0 && !frames[depth - 1].Object && ++frames[depth - 1].Items > MaxArrayItems) throw new ProwlarrSetupProtocolException("Prowlarr JSON contains too many array items.");
            if (reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > MaxStringChars) throw new ProwlarrSetupProtocolException("Prowlarr JSON contains an oversized string.");
            if (depth == 0 && ++roots > 1) throw new JsonException();
        }
        if (depth != 0 || roots != 1 || reader.BytesConsumed != data.Length) throw new JsonException();
    }
    private struct Frame(bool @object)
    {
        public bool Object = @object;
        public int Properties;
        public int Items;
        public HashSet<string>? Names = @object ? new HashSet<string>(StringComparer.Ordinal) : null;
    }

    private Uri Endpoint(string path) => new Uri($"{_baseUri.Scheme}://{_baseUri.GetComponents(UriComponents.Host | UriComponents.Port, UriFormat.UriEscaped)}{RootPath(_baseUri)}/{Prefix}/{path.TrimStart('/')}", UriKind.Absolute);
    private static string RootPath(Uri uri)
    {
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (string.IsNullOrEmpty(path) || path == "/") return string.Empty;
        var root = path.Length > 1 && path[^1] == '/' ? path[..^1] : path;
        return root.StartsWith("/", StringComparison.Ordinal) ? root : "/" + root;
    }
    private static Uri CanonicalBase(Uri uri, string parameter)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Prowlarr URL must be an absolute HTTP URL without credentials, query strings, or fragments.", parameter);
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty, UserName = string.Empty, Password = string.Empty };
        if (builder.Path == "/") builder.Path = string.Empty; return builder.Uri;
    }
    private static bool SameUri(Uri a, Uri b)
    {
        if (!a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) || !a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) || Port(a) != Port(b)) return false;
        return string.Equals(UriPath(a), UriPath(b), StringComparison.Ordinal) && string.Equals(a.GetComponents(UriComponents.Query, UriFormat.UriEscaped), b.GetComponents(UriComponents.Query, UriFormat.UriEscaped), StringComparison.Ordinal);
    }
    private static string UriPath(Uri uri)
    {
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (path == "/") return string.Empty;
        return path.Length > 1 && path[^1] == '/' ? path[..^1] : path;
    }
    private static int Port(Uri u) => u.IsDefaultPort ? (u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : u.Port;
    private static bool Credential(string name) => name.Contains("apikey", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase);
    private static JsonElement RequireArray(JsonElement root, string resource, CancellationToken token) { if (root.ValueKind != JsonValueKind.Array) throw new ProwlarrSetupProtocolException($"Prowlarr {resource} response is not an array."); return root; }

    private static Snapshot Capture(ProwlarrSetupOptions source, CancellationToken token, Func<CancellationToken, Task>? assertMutationLeaseAsync = null)
    {
        ArgumentNullException.ThrowIfNull(source.Indexers);

        // Do not use IReadOnlyCollection.Count here.  In addition to being an
        // untrusted hint, reading it can execute user code before cancellation
        // or the actual enumeration has been observed.
        var inputs = new List<ProwlarrNewznabIndexer>();
        var list = new List<IndexerSnapshot>();
        var serializedInputBytes = 0;
        var inputElements = 0;
        try
        {
            // First bound the actual indexer enumeration. This deliberately
            // happens before any field value is cloned or serialized.
            foreach (var input in source.Indexers)
            {
                token.ThrowIfCancellationRequested();
                if (inputs.Count >= MaxIndexers) throw new ArgumentOutOfRangeException(nameof(source.Indexers));
                ArgumentNullException.ThrowIfNull(input);
                inputs.Add(input);
            }

            foreach (var input in inputs)
            {
                token.ThrowIfCancellationRequested();
                var pairs = new List<KeyValuePair<string, object?>>();
                if (input.Fields is not null)
                    foreach (var pair in input.Fields)
                    {
                        token.ThrowIfCancellationRequested();
                        if (pairs.Count >= MaxIndexerFields) throw new ArgumentOutOfRangeException(nameof(input.Fields));
                        pairs.Add(pair);
                    }

                var fields = new Dictionary<string, ScalarValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in pairs)
                {
                    token.ThrowIfCancellationRequested();
                    var key = pair.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(key) || key.Length > MaxStringChars) throw new ProwlarrSetupConflictException("Prowlarr indexer contains an invalid field name.");
                    ScalarValue value;
                    try
                    {
                        AddInputBytes(checked(Encoding.UTF8.GetByteCount(key) + 4), ref serializedInputBytes);
                        value = ScalarValue.Create(pair.Value, token, ref serializedInputBytes, ref inputElements);
                    }
                    catch (InputSizeExceededException) { throw new ProwlarrSetupProtocolException("Prowlarr indexer input exceeds safety bounds."); }
                    catch (JsonException) { throw new ProwlarrSetupProtocolException("Prowlarr indexer contains an invalid field value."); }
                    catch (NotSupportedException) { throw new ProwlarrSetupProtocolException("Prowlarr indexer contains an invalid field value."); }
                    if (!fields.TryAdd(key, value)) throw new ProwlarrSetupConflictException("Prowlarr indexer contains duplicate fields.");
                }
                list.Add(new IndexerSnapshot(input.Name, input.BaseUrl, input.ApiKey, fields, input.AppProfileId, input.AllowPrivateNetwork));
            }
            return new Snapshot(source.SonarrUrl, source.SonarrApiKey, source.RadarrUrl, source.RadarrApiKey, source.ProwlarrUrl, source.ForceSecretUpdate, assertMutationLeaseAsync ?? source.AssertMutationLeaseAsync, list);
        }
        catch { throw; }
    }

    private static void AddInputBytes(int bytes, ref int total)
    {
        if (bytes < 0 || bytes > MaxInputBytes - total) throw new InputSizeExceededException();
        total += bytes;
    }

    private static void Validate(Snapshot s)
    {
        Url(s.SonarrUrl, "Sonarr URL", true); Url(s.RadarrUrl, "Radarr URL", true); if (s.ProwlarrUrl is not null) Url(s.ProwlarrUrl, "Prowlarr URL", false);
        if (string.IsNullOrWhiteSpace(s.SonarrApiKey) || string.IsNullOrWhiteSpace(s.RadarrApiKey) || s.SonarrApiKey.Length > MaxStringChars || s.RadarrApiKey.Length > MaxStringChars) throw new ArgumentException("Arr API keys are required.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in s.Indexers)
        {
            if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > MaxStringChars || string.IsNullOrWhiteSpace(i.ApiKey) || i.ApiKey.Length > MaxStringChars || !names.Add(i.Name.Trim())) throw new ProwlarrSetupConflictException("Requested Prowlarr indexer names must be unique.");
            Url(i.BaseUrl, "Newznab URL", true);
            if (!NzbWebDAV.Clients.Newznab.NewznabUrlPolicy.IsAllowed(
                    new Uri(i.BaseUrl, UriKind.Absolute), i.AllowPrivateNetwork))
                throw new ArgumentException("Newznab URL does not satisfy the network policy.");
        }
    }
    private static void Url(string value, string description, bool allowQuery) { if (string.IsNullOrWhiteSpace(value) || value.Length > MaxStringChars || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme is not ("http" or "https")) || (!allowQuery && !string.IsNullOrEmpty(uri.Query)) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException($"{description} must be an absolute HTTP URL without credentials or fragments."); }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    private sealed class Snapshot(string sonarrUrl, string sonarrKey, string radarrUrl, string radarrKey, string? prowlarrUrl, bool force, Func<CancellationToken, Task>? assertMutationLeaseAsync, List<IndexerSnapshot> indexers)
    { public string SonarrUrl { get; } = sonarrUrl; public string SonarrApiKey { get; private set; } = sonarrKey; public string RadarrUrl { get; } = radarrUrl; public string RadarrApiKey { get; private set; } = radarrKey; public string? ProwlarrUrl { get; } = prowlarrUrl; public bool Force { get; } = force; public Func<CancellationToken, Task>? AssertMutationLeaseAsync { get; } = assertMutationLeaseAsync; public List<IndexerSnapshot> Indexers { get; } = indexers; public void Clear() { SonarrApiKey = RadarrApiKey = string.Empty; foreach (var i in Indexers) i.Clear(); } }
    private sealed class IndexerSnapshot(string name, string url, string key, Dictionary<string, ScalarValue> fields, int? profile, bool allowPrivateNetwork)
    { public string Name { get; } = name; public string BaseUrl { get; } = url; public string ApiKey { get; private set; } = key; public Dictionary<string, ScalarValue> Fields { get; } = fields; public int? AppProfileId { get; } = profile; public bool AllowPrivateNetwork { get; } = allowPrivateNetwork; public void Clear() { ApiKey = string.Empty; Fields.Clear(); } }
    private sealed record Schema(JsonElement Source, string Implementation, string ImplementationName, string Contract, List<SchemaField> Fields)
    { public IEnumerable<string> Names => Fields.Select(x => x.Name); }
    private sealed record SchemaField(string Name, JsonElement Source, JsonElement Default, bool IsSecret);
    private sealed record Field(string Name, JsonElement Source, bool HasValue, JsonElement Value) { public bool IsMasked => HasValue && Value.ValueKind == JsonValueKind.String && Value.ValueEquals(MaskedSecret); }
    private sealed record Resource(JsonElement Element);
    private sealed record App(string Name, string Implementation, string Url, string ApiKey, Schema Schema, string ProwlarrUrl = "");
    private sealed class Counts { public int Created, Updated, Unchanged; public void Add(Outcome x) { if (x == Outcome.Created) Created++; else if (x == Outcome.Updated) Updated++; else Unchanged++; } }
    private enum Outcome { Created, Updated, Unchanged }
    private sealed class OwnedDocument(JsonDocument document, byte[] buffer) : IDisposable { public JsonDocument Document { get; } = document; private byte[] Buffer { get; } = buffer; public void Dispose() { Document.Dispose(); ArrayPool<byte>.Shared.Return(Buffer, true); } }
    private sealed class Exchange(HttpResponseMessage response) : IDisposable { public HttpResponseMessage Response { get; } = response; public void Dispose() => Response.Dispose(); }
    private sealed class InputSizeExceededException : Exception;
    private enum ScalarKind { Null, String, Boolean, Signed, Unsigned, Decimal, Double, Single }
    private sealed class ScalarValue
    {
        private readonly ScalarKind _kind; private readonly string? _text; private readonly long _signed; private readonly ulong _unsigned; private readonly decimal _decimal; private readonly double _double; private readonly bool _boolean;
        private ScalarValue(ScalarKind kind, string? text = null, long signed = 0, ulong unsigned = 0, decimal dec = 0, double number = 0, bool boolean = false) { _kind = kind; _text = text; _signed = signed; _unsigned = unsigned; _decimal = dec; _double = number; _boolean = boolean; }
        public static ScalarValue Create(object? value, CancellationToken token, ref int total, ref int elements)
        {
            token.ThrowIfCancellationRequested();
            ScalarValue result;
            switch (value)
            {
                case null: result = new(ScalarKind.Null); break;
                case string text when text.Length <= MaxStringChars: result = new(ScalarKind.String, text); break;
                case string: throw new InputSizeExceededException();
                case bool boolean: result = new(ScalarKind.Boolean, boolean: boolean); break;
                case byte number: result = new(ScalarKind.Unsigned, unsigned: number); break;
                case ushort number: result = new(ScalarKind.Unsigned, unsigned: number); break;
                case uint number: result = new(ScalarKind.Unsigned, unsigned: number); break;
                case ulong number: result = new(ScalarKind.Unsigned, unsigned: number); break;
                case sbyte number: result = new(ScalarKind.Signed, signed: number); break;
                case short number: result = new(ScalarKind.Signed, signed: number); break;
                case int number: result = new(ScalarKind.Signed, signed: number); break;
                case long number: result = new(ScalarKind.Signed, signed: number); break;
                case decimal number: result = new(ScalarKind.Decimal, dec: number); break;
                case double number when !double.IsNaN(number) && !double.IsInfinity(number): result = new(ScalarKind.Double, number: number); break;
                case float number when !float.IsNaN(number) && !float.IsInfinity(number): result = new(ScalarKind.Single, number: number); break;
                default: throw new NotSupportedException();
            }
            if (++elements > MaxElements) throw new InputSizeExceededException();
            var bytes = result.EstimatedBytes();
            AddInputBytes(bytes, ref total);
            return result;
        }
        private int EstimatedBytes() => _kind switch
        {
            ScalarKind.Null => 4, ScalarKind.Boolean => _boolean ? 4 : 5, ScalarKind.Signed => 24, ScalarKind.Unsigned => 24,
            ScalarKind.Decimal => 32, ScalarKind.Double or ScalarKind.Single => 32,
            ScalarKind.String => checked(Encoding.UTF8.GetByteCount(_text!) * 6 + 2), _ => throw new InvalidOperationException()
        };
        public void WriteTo(Utf8JsonWriter writer)
        {
            switch (_kind)
            {
                case ScalarKind.Null: writer.WriteNullValue(); break; case ScalarKind.String: writer.WriteStringValue(_text); break; case ScalarKind.Boolean: writer.WriteBooleanValue(_boolean); break;
                case ScalarKind.Signed: writer.WriteNumberValue(_signed); break; case ScalarKind.Unsigned: writer.WriteNumberValue(_unsigned); break; case ScalarKind.Decimal: writer.WriteNumberValue(_decimal); break;
                case ScalarKind.Double: writer.WriteNumberValue(_double); break; case ScalarKind.Single: writer.WriteNumberValue((float)_double); break;
            }
        }
    }
    private sealed class OwnedPayload : IBufferWriter<byte>, IDisposable
    {
        private readonly int _maximum; private byte[]? _buffer; private int _length;
        public OwnedPayload(int maximum) { _maximum = maximum; _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximum, 4096)); }
        public int Length => _length; public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _length); public ArraySegment<byte> Segment => _buffer is null ? default : new ArraySegment<byte>(_buffer, 0, _length);
        public void Advance(int count) { if (_buffer is null || count < 0 || count > _buffer.Length - _length || count > _maximum - _length) throw new InputSizeExceededException(); _length += count; }
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer!.AsMemory(_length); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer!.AsSpan(_length); }
        private void Ensure(int sizeHint)
        {
            if (_buffer is null || sizeHint < 0 || sizeHint > _maximum - _length) throw new InputSizeExceededException();
            if (_buffer.Length - _length >= sizeHint) return;
            var next = Math.Min(_maximum, Math.Max(_length + sizeHint, Math.Min(_maximum, _buffer.Length * 2)));
            var replacement = ArrayPool<byte>.Shared.Rent(next); _buffer.AsSpan(0, _length).CopyTo(replacement); Array.Clear(_buffer, 0, _length); ArrayPool<byte>.Shared.Return(_buffer, true); _buffer = replacement;
        }
        public void Dispose() { if (_buffer is null) return; Array.Clear(_buffer, 0, _length); ArrayPool<byte>.Shared.Return(_buffer, true); _buffer = null; _length = 0; }
    }
    private sealed class SecretJsonContent : HttpContent
    {
        private readonly OwnedPayload _payload;
        public SecretJsonContent(OwnedPayload payload)
        {
            _payload = payload;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { var segment = _payload.Segment; return stream.WriteAsync(segment.Array!, segment.Offset, segment.Count); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) { var segment = _payload.Segment; return stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken); }
        protected override bool TryComputeLength(out long length) { length = _payload.Length; return true; }
        protected override void Dispose(bool disposing) { if (disposing) _payload.Dispose(); base.Dispose(disposing); }
    }
}

public sealed class ProwlarrSetupHttpException : HttpRequestException
{
    public ProwlarrSetupHttpException(string path, HttpStatusCode statusCode) : base($"Prowlarr request failed ({(int)statusCode} {statusCode}).") { StatusCode = statusCode; }
    public new HttpStatusCode StatusCode { get; }
}
