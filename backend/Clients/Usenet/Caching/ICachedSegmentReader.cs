using NzbWebDAV.Clients.Usenet.Models;

namespace NzbWebDAV.Clients.Usenet.Caching;

public interface ICachedSegmentReader
{
    bool HasCachedBody(string segmentId);
    bool TryReadCachedBody(string segmentId, out UsenetDecodedBodyResponse response);
}
