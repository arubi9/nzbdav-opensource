using System.Net;

namespace NzbWebDAV.Clients;

/// <summary>
/// Creates clients for setup traffic. Redirects are never followed. The
/// handler is process-long-lived so short-lived setup clients do not create a
/// new connection pool for every status check.
/// </summary>
public static class SetupHttpClientFactory
{
    private static int _handlerCreationCount;
    private static readonly SocketsHttpHandler SharedHandler = CreateSharedHandler();

    internal static int HandlerCreationCountForTests => Volatile.Read(ref _handlerCreationCount);
    internal static SocketsHttpHandler SharedHandlerForTests => SharedHandler;

    private static SocketsHttpHandler CreateSharedHandler()
    {
        Interlocked.Increment(ref _handlerCreationCount);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            // Setup callers apply their own bounded operation deadlines.
            // Keeping this transport timeout infinite avoids a hidden second
            // deadline.
            ConnectTimeout = Timeout.InfiniteTimeSpan,
        };
    }

    public static HttpClient Create()
        // HttpClient disposal must not tear down the shared handler. This also
        // preserves disposable/injected test clients without pooling leaks.
        => new(SharedHandler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static bool IsRedirect(HttpResponseMessage response)
        => (int)response.StatusCode is >= 300 and < 400;
}
