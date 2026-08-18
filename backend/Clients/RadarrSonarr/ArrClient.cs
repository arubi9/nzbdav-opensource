using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class ArrClient(string host, string apiKey)
{
    protected static readonly HttpClient HttpClient = SetupHttpClientFactory.Create();
    private const string BasePath = "/api/v3";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

    public string Host { get; } = ValidateHost(host);
    private string ApiKey { get; } = ValidateApiKey(apiKey);

    public Task<ArrApiInfoResponse> GetApiInfo() => GetRoot<ArrApiInfoResponse>("/api");
    public Task<ArrApiInfoResponse> GetApiInfo(CancellationToken cancellationToken) => GetRoot<ArrApiInfoResponse>("/api", cancellationToken);
    public virtual Task<bool> RemoveAndSearch(string symlinkOrStrmPath) => throw new InvalidOperationException();
    public virtual Task<bool> RemoveAndSearch(string symlinkOrStrmPath, CancellationToken cancellationToken)
        => RemoveAndSearch(symlinkOrStrmPath);
    public Task<List<ArrRootFolder>> GetRootFolders() => Get<List<ArrRootFolder>>("/rootfolder");
    public Task<List<ArrRootFolder>> GetRootFolders(CancellationToken cancellationToken) => Get<List<ArrRootFolder>>("/rootfolder", cancellationToken);
    public Task<List<ArrDownloadClient>> GetDownloadClientsAsync() => Get<List<ArrDownloadClient>>("/downloadClient");
    public Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken cancellationToken) => Get<List<ArrDownloadClient>>("/downloadClient", cancellationToken);
    public Task<ArrCommand> RefreshMonitoredDownloads() => CommandAsync(new { name = "RefreshMonitoredDownloads" });
    public Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken cancellationToken) => CommandAsync(new { name = "RefreshMonitoredDownloads" }, cancellationToken);
    public Task<ArrQueueStatus> GetQueueStatusAsync() => Get<ArrQueueStatus>("/queue/status");
    public Task<ArrQueueStatus> GetQueueStatusAsync(CancellationToken cancellationToken) => Get<ArrQueueStatus>("/queue/status", cancellationToken);
    public Task<ArrQueue<ArrQueueRecord>> GetQueueAsync() => Get<ArrQueue<ArrQueueRecord>>("/queue?protocol=usenet&pageSize=5000");
    public Task<ArrQueue<ArrQueueRecord>> GetQueueAsync(CancellationToken cancellationToken) => Get<ArrQueue<ArrQueueRecord>>("/queue?protocol=usenet&pageSize=5000", cancellationToken);
    public async Task<int> GetQueueCountAsync() => (await Get<ArrQueue<ArrQueueRecord>>("/queue?pageSize=1").ConfigureAwait(false)).TotalRecords;
    public async Task<int> GetQueueCountAsync(CancellationToken cancellationToken) => (await Get<ArrQueue<ArrQueueRecord>>("/queue?pageSize=1", cancellationToken).ConfigureAwait(false)).TotalRecords;
    public Task<HttpStatusCode> DeleteQueueRecord(int id, DeleteQueueRecordRequest request) => Delete($"/queue/{id}", request.GetQueryParams());
    public Task<HttpStatusCode> DeleteQueueRecord(int id, DeleteQueueRecordRequest request, CancellationToken cancellationToken) => Delete($"/queue/{id}", request.GetQueryParams(), cancellationToken);
    public Task<HttpStatusCode> DeleteQueueRecord(int id, ArrConfig.QueueAction request) => request is not ArrConfig.QueueAction.DoNothing ? Delete($"/queue/{id}", new DeleteQueueRecordRequest(request).GetQueryParams()) : Task.FromResult(HttpStatusCode.OK);
    public Task<HttpStatusCode> DeleteQueueRecord(int id, ArrConfig.QueueAction request, CancellationToken cancellationToken) => request is not ArrConfig.QueueAction.DoNothing ? Delete($"/queue/{id}", new DeleteQueueRecordRequest(request).GetQueryParams(), cancellationToken) : Task.FromResult(HttpStatusCode.OK);
    public Task<ArrCommand> CommandAsync(object command) => Post<ArrCommand>("/command", command);
    public Task<ArrCommand> CommandAsync(object command, CancellationToken cancellationToken) => Post<ArrCommand>("/command", command, cancellationToken);
    protected Task<T> Get<T>(string path) => GetRoot<T>($"{BasePath}{path}");
    protected Task<T> Get<T>(string path, CancellationToken cancellationToken) => GetRoot<T>($"{BasePath}{path}", cancellationToken);

    protected async Task<T> GetRoot<T>(string rootPath, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Host}{rootPath}", null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<T> Post<T>(string path, object body, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, GetRequestUri(path), body, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<HttpStatusCode> Delete(string path, Dictionary<string, string>? queryParams = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, GetRequestUri(path, queryParams), null, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    private string GetRequestUri(string path, Dictionary<string, string>? queryParams = null)
    {
        queryParams ??= new Dictionary<string, string>();
        var resource = $"{Host}{BasePath}{path}";
        var query = queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        var queryString = string.Join("&", query);
        return queryString.Length == 0 ? resource : $"{resource}?{queryString}";
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, object? body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Api-Key", ApiKey);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        try
        {
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (SetupHttpClientFactory.IsRedirect(response))
            {
                var code = response.StatusCode;
                response.Dispose();
                throw new ArrClientException(ArrClientFailure.Redirect, code);
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode;
                response.Dispose();
                throw new ArrClientException(code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ArrClientFailure.Unauthorized : ArrClientFailure.Upstream, code);
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArrClientException(ArrClientFailure.Timeout);
        }
        catch (HttpRequestException exception) when (exception is not ArrClientException)
        {
            throw new ArrClientException(ArrClientFailure.Unreachable, inner: exception);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var readToken = timeout.Token;
        try
        {
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new ArrClientException(ArrClientFailure.ResponseTooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(readToken).ConfigureAwait(false);
            using var output = new MemoryStream(Math.Min(MaxResponseBytes, 8192));
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), readToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaxResponseBytes) throw new ArrClientException(ArrClientFailure.ResponseTooLarge);
                await output.WriteAsync(buffer.AsMemory(0, read), readToken).ConfigureAwait(false);
            }
            try
            {
                return JsonSerializer.Deserialize<T>(output.GetBuffer().AsSpan(0, checked((int)output.Length)), JsonOptions)
                    ?? throw new ArrClientException(ArrClientFailure.InvalidResponse);
            }
            catch (JsonException exception)
            {
                throw new ArrClientException(ArrClientFailure.InvalidResponse, inner: exception);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArrClientException(ArrClientFailure.Timeout);
        }
    }

    private static string ValidateHost(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Arr host must be an absolute HTTP URL without credentials or query parameters.", nameof(value));
        return uri.ToString().TrimEnd('/');
    }

    private static string ValidateApiKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Length > 512)
            throw new ArgumentException("An Arr API key is required.", nameof(value));
        return value;
    }
}

public enum ArrClientFailure { Unauthorized, Redirect, Timeout, Unreachable, Upstream, ResponseTooLarge, InvalidResponse }
public sealed class ArrClientException(ArrClientFailure failure, HttpStatusCode? statusCode = null, Exception? inner = null)
    : HttpRequestException("Arr connection failed.", inner)
{
    public ArrClientFailure Failure { get; } = failure;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
