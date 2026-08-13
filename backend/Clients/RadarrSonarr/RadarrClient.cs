using System.Collections.Concurrent;
using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class RadarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    // ConcurrentDictionary because concurrent queue-item processing can hit
    // this cache in parallel. Previously a plain Dictionary without locking
    // — race condition on resize, intermittent hangs/corruption under load.
    private static readonly ConcurrentDictionary<string, int> SymlinkOrStrmToMovieIdCache = new();

    public Task<RadarrMovie> GetMovieAsync(int id, CancellationToken cancellationToken = default) =>
        Get<RadarrMovie>($"/movie/{id}", cancellationToken);

    public Task<List<RadarrMovie>> GetMoviesAsync(CancellationToken cancellationToken = default) =>
        Get<List<RadarrMovie>>("/movie", cancellationToken);

    public Task<RadarrQueue> GetRadarrQueueAsync(CancellationToken cancellationToken = default) =>
        Get<RadarrQueue>("/queue?protocol=usenet&pageSize=5000", cancellationToken);

    public Task<HttpStatusCode> DeleteMovieFile(int id, CancellationToken cancellationToken = default) =>
        Delete($"/moviefile/{id}", cancellationToken: cancellationToken);

    public Task<ArrCommand> SearchMovieAsync(int id, CancellationToken cancellationToken = default) =>
        CommandAsync(new { name = "MoviesSearch", movieIds = new List<int> { id } }, cancellationToken);

    public override Task<bool> RemoveAndSearch(string symlinkOrStrmPath) =>
        RemoveAndSearch(symlinkOrStrmPath, CancellationToken.None);

    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath, CancellationToken cancellationToken)
    {
        var mediaIds = await GetMediaIds(symlinkOrStrmPath, cancellationToken).ConfigureAwait(false);
        if (mediaIds == null) return false;

        if (await DeleteMovieFile(mediaIds.Value.movieFileId, cancellationToken).ConfigureAwait(false) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete movie file `{symlinkOrStrmPath}` from radarr instance `{Host}`.");

        await SearchMovieAsync(mediaIds.Value.movieId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<(int movieFileId, int movieId)?> GetMediaIds(string symlinkOrStrmPath, CancellationToken cancellationToken)
    {
        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkOrStrmToMovieIdCache.TryGetValue(symlinkOrStrmPath, out var movieId))
        {
            var movie = await GetMovieAsync(movieId, cancellationToken).ConfigureAwait(false);
            if (movie.MovieFile?.Path == symlinkOrStrmPath)
                return (movie.MovieFile.Id!, movieId);
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie-id and movie-file-id
        var allMovies = await GetMoviesAsync(cancellationToken).ConfigureAwait(false);
        (int movieFileId, int movieId)? result = null;
        foreach (var movie in allMovies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
                SymlinkOrStrmToMovieIdCache[movieFile.Path] = movie.Id;
            if (movieFile?.Path == symlinkOrStrmPath)
                result = (movieFile.Id!, movie.Id);
        }

        return result;
    }
}
