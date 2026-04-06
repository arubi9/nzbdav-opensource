using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Nzbdav;

public class NzbdavMediaSourceProvider : IMediaSourceProvider
{
    private readonly ILogger<NzbdavMediaSourceProvider> _logger;

    public NzbdavMediaSourceProvider(ILogger<NzbdavMediaSourceProvider> logger)
    {
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken ct)
    {
        if (!item.ProviderIds.TryGetValue("NzbdavId", out var idStr) || !Guid.TryParse(idStr, out var nzbdavId))
            return [];

        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.NzbdavBaseUrl))
            return [];

        try
        {
            using var client = new NzbdavApiClient(config);
            var meta = await client.GetMetaAsync(nzbdavId, ct).ConfigureAwait(false);
            if (meta is null)
                return [];

            var streamUrl = client.GetSignedStreamUrl(nzbdavId, meta.StreamToken ?? string.Empty);

            return
            [
                new MediaSourceInfo
                {
                    Id = nzbdavId.ToString("N"),
                    Path = streamUrl,
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    IsRemote = true,
                    SupportsDirectStream = true,
                    SupportsDirectPlay = true,
                    SupportsTranscoding = true,
                    RequiresOpening = false,
                    RequiresClosing = false,
                    Container = Path.GetExtension(meta.Name)?.TrimStart('.') ?? "mkv",
                    Name = $"NZBDAV - {meta.Name}",
                    Size = meta.FileSize
                }
            ];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get NZBDAV media source for {Item}", item.Name);
            return [];
        }
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken ct)
        => throw new NotSupportedException("NZBDAV uses HTTP protocol sources.");
}
