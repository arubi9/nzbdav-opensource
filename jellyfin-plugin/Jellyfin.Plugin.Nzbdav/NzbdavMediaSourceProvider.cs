using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Returns an authoritative <see cref="MediaSourceInfo"/> for NZBDAV-managed
/// <c>.strm</c> items, populated from the pre-extracted <c>.mediainfo.json</c>
/// sidecar. Setting <see cref="MediaSourceInfo.SupportsProbing"/> to <c>false</c>
/// tells Jellyfin's PlaybackInfo pipeline to trust the values we hand it and
/// skip the 1&nbsp;GB / 200&nbsp;MB ffprobe that otherwise runs against the
/// remote NZBDAV stream URL at play time (and was the dominant start-time cost
/// on large UHD files).
/// </summary>
public sealed class NzbdavMediaSourceProvider : IMediaSourceProvider
{
    private readonly ILogger<NzbdavMediaSourceProvider> _logger;
    private readonly Func<NzbdavOperationConfiguration?> _configurationAccessor;

    public NzbdavMediaSourceProvider(ILogger<NzbdavMediaSourceProvider> logger)
        : this(logger, NzbdavOperationConfigurationAccessor.CaptureFromPlugin)
    {
    }

    // One accessor supplies an immutable configuration lease for the entire
    // operation. Keeping this seam explicit makes provider tests independent
    // of Jellyfin's mutable plugin singleton.
    internal NzbdavMediaSourceProvider(
        ILogger<NzbdavMediaSourceProvider> logger,
        Func<NzbdavOperationConfiguration?> configurationAccessor)
    {
        _logger = logger;
        _configurationAccessor = configurationAccessor;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(
        BaseItem item, CancellationToken cancellationToken)
    {
        if (item is null || !IsNzbdavStrmItem(item))
            return [];

        try
        {
            // This lease covers recovery, the tombstone check, and every byte
            // read used to build the source. In particular, do not release it
            // after recovery and then check/read the two pathnames separately:
            // a scheduled reconcile could retire the journal in that window.
            // Snapshot before the first await. A queued provider operation
            // must never combine the root from one configuration revision with
            // the endpoint from another after the media gate becomes available.
            var operationConfiguration = _configurationAccessor();
            if (operationConfiguration is null || !operationConfiguration.IsValid)
                return [];

            using var gate = await NzbdavLibrarySyncTask.EnterMediaGateAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!NzbdavLibrarySyncTask.RecoverForMediaUnderGate(
                    operationConfiguration.LibraryPath, item.Path, cancellationToken))
                return [];

            // Linux stale reconciliation retains the source pathname and uses
            // an identity+content-bound tombstone. Do not serve that retained
            // inode; a foreign replacement at the same pathname remains visible.
            if (NzbdavLibrarySyncTask.IsLogicallyTombstoned(operationConfiguration.LibraryPath, item.Path))
                return [];

            // Jellyfin's BaseItem.Id is a library/database identity, not the
            // NZBDAV manifest identity. The completed marker binds the stream
            // URL GUID to its own manifest item id.
            if (!NzbdavLibrarySyncTask.TryReadManagedMediaForProvider(
                    operationConfiguration.LibraryPath,
                    item.Path,
                    operationConfiguration.NzbdavBaseUrl,
                    Guid.Empty,
                    out var streamUrl,
                    out var probeBytes))
                return [];

            if (!TryDeserializeProbe(probeBytes, out var probe) || probe is null)
                return [];

            // BuildMediaSource is intentionally inside the same lease as the
            // reads above. Jellyfin may inspect the returned object only after
            // this method returns, but it never receives a partially-built
            // source from a path that changed during construction.
            return [BuildMediaSource(item, streamUrl, probe)];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to build NZBDAV media source for {ItemPath}; refusing unproven ownership",
                item?.Path);
            return [];
        }
    }

    public Task<ILiveStream> OpenMediaSource(
        string openToken,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException(
            "NZBDAV streams are HTTP range-addressable; Jellyfin never needs to open them as live streams.");

    private static bool IsNzbdavStrmItem(BaseItem item)
    {
        if (item is not Video) return false;
        var path = item.Path;
        if (string.IsNullOrEmpty(path)) return false;
        return path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
    }

    private MediaSourceInfo BuildMediaSource(BaseItem item, string streamUrl, FfprobeOutput probe)
    {
        var fmt = probe.Format;
        var streams = (probe.Streams ?? []).Select(ConvertStream).Where(s => s is not null).Select(s => s!).ToList();
        var defaultAudio = streams.FirstOrDefault(s =>
            s.Type == MediaStreamType.Audio && s.IsDefault);
        defaultAudio ??= streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        var container = GuessContainer(fmt?.FormatName, item.Path);

        long? size = null;
        if (long.TryParse(fmt?.Size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize))
            size = parsedSize;

        long? runTimeTicks = null;
        if (double.TryParse(fmt?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var durSec))
            runTimeTicks = (long)(durSec * TimeSpan.TicksPerSecond);

        int? bitrate = null;
        if (int.TryParse(fmt?.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBitrate))
            bitrate = parsedBitrate;

        var info = new MediaSourceInfo
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Name = item.Name,
            IsRemote = true,
            Container = container,
            Size = size,
            Bitrate = bitrate,
            RunTimeTicks = runTimeTicks,
            MediaStreams = streams,
            SupportsProbing = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = false,
            RequiresOpening = false,
            RequiresClosing = false,
            ReadAtNativeFramerate = false,
            DefaultAudioStreamIndex = defaultAudio?.Index
        };

        return info;
    }

    private static MediaStream? ConvertStream(FfprobeStream s)
    {
        var type = s.CodecType?.ToLowerInvariant() switch
        {
            "video" => MediaStreamType.Video,
            "audio" => MediaStreamType.Audio,
            "subtitle" => MediaStreamType.Subtitle,
            _ => (MediaStreamType?)null
        };
        if (type is null) return null;

        var stream = new MediaStream
        {
            Index = s.Index,
            Type = type.Value,
            Codec = s.CodecName ?? string.Empty,
            CodecTag = s.CodecTagString,
            Profile = s.Profile,
            IsDefault = GetDisposition(s, "default") == 1,
            IsForced = GetDisposition(s, "forced") == 1,
            IsHearingImpaired = GetDisposition(s, "hearing_impaired") == 1
        };

        if (s.Tags is not null)
        {
            if (s.Tags.TryGetValue("language", out var lang))
                stream.Language = lang;
            if (s.Tags.TryGetValue("title", out var title))
                stream.Title = title;
        }

        if (int.TryParse(s.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var br))
            stream.BitRate = br;

        switch (type.Value)
        {
            case MediaStreamType.Video:
                stream.Width = s.Width;
                stream.Height = s.Height;
                stream.PixelFormat = s.PixelFormat;
                stream.ColorRange = s.ColorRange;
                stream.ColorSpace = s.ColorSpace;
                stream.ColorTransfer = s.ColorTransfer;
                stream.ColorPrimaries = s.ColorPrimaries;
                stream.AspectRatio = s.DisplayAspectRatio;
                stream.Level = s.Level;
                stream.RefFrames = s.Refs;
                stream.IsAVC = string.Equals(s.IsAvc, "true", StringComparison.OrdinalIgnoreCase);
                stream.NalLengthSize = s.NalLengthSize;
                stream.AverageFrameRate = ParseFrameRate(s.AverageFrameRate);
                stream.RealFrameRate = ParseFrameRate(s.RealFrameRate);
                if (int.TryParse(s.BitsPerRawSample, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitDepth))
                    stream.BitDepth = bitDepth;
                break;

            case MediaStreamType.Audio:
                stream.Channels = s.Channels;
                stream.ChannelLayout = s.ChannelLayout;
                if (int.TryParse(s.SampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sr))
                    stream.SampleRate = sr;
                if (s.BitsPerSample is > 0) stream.BitDepth = s.BitsPerSample;
                break;

            case MediaStreamType.Subtitle:
                // Default/forced already set above. Nothing extra needed here
                // because Jellyfin plays subtitles out of the stream URL using
                // the Index we provide.
                break;
        }

        return stream;
    }

    private static bool TryDeserializeProbe(byte[] bytes, out FfprobeOutput? probe)
    {
        try
        {
            probe = FfprobeJsonParser.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            probe = null;
            return false;
        }
    }

    private static int GetDisposition(FfprobeStream s, string key)
    {
        if (s.Disposition is null) return 0;
        return s.Disposition.TryGetValue(key, out var value) ? value : 0;
    }

    private static float? ParseFrameRate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var slash = value.IndexOf('/');
        if (slash < 0)
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct)
                ? direct
                : null;

        var numStr = value[..slash];
        var denStr = value[(slash + 1)..];
        if (!float.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            || !float.TryParse(denStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            || den == 0f)
        {
            return null;
        }
        return num / den;
    }

    private static string GuessContainer(string? formatName, string? itemPath)
    {
        if (!string.IsNullOrEmpty(formatName))
        {
            // ffprobe reports comma-separated list (e.g. "mov,mp4,m4a,3gp,3g2,mj2"); first entry is good enough.
            var first = formatName.Split(',', 2)[0].Trim();
            if (!string.IsNullOrEmpty(first)) return first;
        }
        if (!string.IsNullOrEmpty(itemPath))
        {
            var ext = Path.GetExtension(itemPath);
            if (!string.IsNullOrEmpty(ext) && ext.Length > 1)
                return ext[1..].ToLowerInvariant();
        }
        return "mkv";
    }
}
