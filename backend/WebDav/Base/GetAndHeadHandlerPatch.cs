using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Handlers;
using NWebDav.Server.Helpers;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Services;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Implementation of the GET and HEAD method.
/// </summary>
/// <remarks>
/// The specification of the WebDAV GET and HEAD methods for collections
/// can be found in the
/// <see href="http://www.webdav.org/specs/rfc2518.html#rfc.section.8.4">
/// WebDAV specification
/// </see>.
/// </remarks>
public class GetAndHeadHandlerPatch : IRequestHandler
{
    private readonly IStore _store;
    private readonly StreamExecutionService _streamService;

    public GetAndHeadHandlerPatch(IStore store, StreamExecutionService streamService)
    {
        _store = store;
        _streamService = streamService;
    }
    
    /// <summary>
    /// Handle a GET or HEAD request.
    /// </summary>
    /// <param name="httpContext">
    /// The HTTP context of the request.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous GET or HEAD operation. The
    /// task will always return <see langword="true"/> upon completion.
    /// </returns>
    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        // Obtain request and response
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Obtain the WebDAV collection
        var entry = await _store.GetItemAsync(request.GetUri(), httpContext.RequestAborted).ConfigureAwait(false);
        if (entry == null)
        {
            // Set status to not found
            response.SetStatus(DavStatusCode.NotFound);
            return true;
        }

        // ETag might be used for a conditional request
        string? etag = null;

        // Add non-expensive headers based on properties
        var propertyManager = entry.PropertyManager;
        if (propertyManager != null)
        {
            // Add Last-Modified header
            var lastModifiedUtc = (string?)await propertyManager.GetPropertyAsync(entry, DavGetLastModified<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (lastModifiedUtc != null)
                response.Headers.LastModified = lastModifiedUtc;

            // Add ETag
            etag = (string?)await propertyManager.GetPropertyAsync(entry, DavGetEtag<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (etag != null)
                response.Headers.ETag = etag;

            // Add type
            var contentType = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentType<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentType != null)
                response.ContentType = contentType;

            // Add language
            var contentLanguage = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentLanguage<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentLanguage != null)
                response.Headers.ContentLanguage = contentLanguage;
        }

        // Stream the actual entry
        try
        {
            var stream = await entry.GetReadableStreamAsync(httpContext.RequestAborted).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                if (stream != Stream.Null)
                {
                    // Do not return the actual item data if ETag matches
                    if (etag != null && request.Headers.IfNoneMatch == etag)
                    {
                        response.ContentLength = 0;
                        response.SetStatus(DavStatusCode.NotModified);
                        return true;
                    }

                    await _streamService.ServeStreamAsync(
                        stream,
                        entry.Name,
                        response,
                        request,
                        httpContext.RequestAborted
                    ).ConfigureAwait(false);
                }
                else
                {
                    // Set the response
                    response.SetStatus(DavStatusCode.NoContent);
                }
            }
        }
        finally
        {
            if (httpContext.Items.TryGetValue(SegmentFetchContext.HttpContextItemKey, out var fetchContextScope)
                && fetchContextScope is IDisposable disposable)
            {
                disposable.Dispose();
                httpContext.Items.Remove(SegmentFetchContext.HttpContextItemKey);
            }
        }

        return true;
    }
}
