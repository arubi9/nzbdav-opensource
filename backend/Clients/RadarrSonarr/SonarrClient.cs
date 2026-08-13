using System.Collections.Concurrent;
using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class SonarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    // ConcurrentDictionary because concurrent queue-item processing can hit
    // these caches in parallel. Previously a plain Dictionary without locking
    // — race condition on resize, intermittent hangs/corruption under load.
    private static readonly ConcurrentDictionary<string, int> SeriesPathToSeriesIdCache = new();
    private static readonly ConcurrentDictionary<string, int> SymlinkOrStrmToEpisodeFileIdCache = new();

    public Task<SonarrQueue> GetSonarrQueueAsync(CancellationToken cancellationToken = default) =>
        Get<SonarrQueue>("/queue?protocol=usenet&pageSize=5000", cancellationToken);

    public Task<List<SonarrSeries>> GetAllSeries(CancellationToken cancellationToken = default) =>
        Get<List<SonarrSeries>>("/series", cancellationToken);

    public Task<SonarrSeries> GetSeries(int seriesId, CancellationToken cancellationToken = default) =>
        Get<SonarrSeries>($"/series/{seriesId}", cancellationToken);

    public Task<SonarrEpisodeFile> GetEpisodeFile(int episodeFileId, CancellationToken cancellationToken = default) =>
        Get<SonarrEpisodeFile>($"/episodefile/{episodeFileId}", cancellationToken);

    public Task<List<SonarrEpisodeFile>> GetAllEpisodeFiles(int seriesId, CancellationToken cancellationToken = default) =>
        Get<List<SonarrEpisodeFile>>($"/episodefile?seriesId={seriesId}", cancellationToken);

    public Task<List<SonarrEpisode>> GetEpisodesFromEpisodeFileId(int episodeFileId, CancellationToken cancellationToken = default) =>
        Get<List<SonarrEpisode>>($"/episode?episodeFileId={episodeFileId}", cancellationToken);

    public Task<HttpStatusCode> DeleteEpisodeFile(int episodeFileId, CancellationToken cancellationToken = default) =>
        Delete($"/episodefile/{episodeFileId}", cancellationToken: cancellationToken);

    public Task<ArrCommand> SearchEpisodesAsync(List<int> episodeIds, CancellationToken cancellationToken = default) =>
        CommandAsync(new { name = "EpisodeSearch", episodeIds }, cancellationToken);

    public override Task<bool> RemoveAndSearch(string symlinkOrStrmPath) =>
        RemoveAndSearch(symlinkOrStrmPath, CancellationToken.None);

    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath, CancellationToken cancellationToken)
    {
        var mediaIds = await GetMediaIds(symlinkOrStrmPath, cancellationToken).ConfigureAwait(false);
        if (mediaIds == null) return false;
        if (await DeleteEpisodeFile(mediaIds.Value.episodeFileId, cancellationToken).ConfigureAwait(false) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete episode file `{symlinkOrStrmPath}` from sonarr instance `{Host}`.");
        await SearchEpisodesAsync(mediaIds.Value.episodeIds, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<(int episodeFileId, List<int> episodeIds)?> GetMediaIds(string symlinkOrStrmPath, CancellationToken cancellationToken)
    {
        var episodeFileId = await GetEpisodeFileId(symlinkOrStrmPath, cancellationToken).ConfigureAwait(false);
        if (episodeFileId == null) return null;
        var episodes = await GetEpisodesFromEpisodeFileId(episodeFileId.Value, cancellationToken).ConfigureAwait(false);
        var episodeIds = episodes.Select(x => x.Id).ToList();
        return episodeIds.Count == 0 ? null : (episodeFileId.Value, episodeIds);
    }

    private async Task<int?> GetEpisodeFileId(string symlinkOrStrmPath, CancellationToken cancellationToken)
    {
        if (SymlinkOrStrmToEpisodeFileIdCache.TryGetValue(symlinkOrStrmPath, out var episodeFileId))
        {
            var episodeFile = await GetEpisodeFile(episodeFileId, cancellationToken).ConfigureAwait(false);
            if (episodeFile.Path == symlinkOrStrmPath) return episodeFileId;
        }

        var seriesId = await GetSeriesId(symlinkOrStrmPath, cancellationToken).ConfigureAwait(false);
        if (seriesId == null) return null;
        int? result = null;
        foreach (var episodeFile in await GetAllEpisodeFiles(seriesId.Value, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SymlinkOrStrmToEpisodeFileIdCache[episodeFile.Path!] = episodeFile.Id;
            if (episodeFile.Path == symlinkOrStrmPath) result = episodeFile.Id;
        }
        return result;
    }

    private async Task<int?> GetSeriesId(string symlinkOrStrmPath, CancellationToken cancellationToken)
    {
        var cachedSeriesId = PathUtil.GetAllParentDirectories(symlinkOrStrmPath)
            .Where(x => SeriesPathToSeriesIdCache.ContainsKey(x))
            .Select(x => SeriesPathToSeriesIdCache[x])
            .Select(x => (int?)x)
            .FirstOrDefault();
        if (cachedSeriesId != null)
        {
            var series = await GetSeries(cachedSeriesId.Value, cancellationToken).ConfigureAwait(false);
            if (symlinkOrStrmPath.StartsWith(series.Path!)) return cachedSeriesId;
        }

        int? result = null;
        foreach (var series in await GetAllSeries(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SeriesPathToSeriesIdCache[series.Path!] = series.Id;
            if (symlinkOrStrmPath.StartsWith(series.Path!)) result = series.Id;
        }
        return result;
    }
}
