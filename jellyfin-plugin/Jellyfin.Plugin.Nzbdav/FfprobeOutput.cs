using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Minimal shape of `ffprobe -print_format json -show_streams -show_format`
/// output needed to build a Jellyfin MediaSourceInfo. Fields not used by the
/// provider are intentionally omitted; System.Text.Json ignores them.
/// </summary>
internal sealed class FfprobeOutput
{
    [JsonPropertyName("streams")]
    public FfprobeStream[] Streams { get; set; } = [];

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; set; }
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("nb_streams")]
    public int NumberOfStreams { get; set; }

    [JsonPropertyName("format_name")]
    public string? FormatName { get; set; }

    [JsonPropertyName("format_long_name")]
    public string? FormatLongName { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    [JsonPropertyName("bit_rate")]
    public string? BitRate { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("codec_long_name")]
    public string? CodecLongName { get; set; }

    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    [JsonPropertyName("codec_type")]
    public string? CodecType { get; set; }

    [JsonPropertyName("codec_tag_string")]
    public string? CodecTagString { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("coded_width")]
    public int? CodedWidth { get; set; }

    [JsonPropertyName("coded_height")]
    public int? CodedHeight { get; set; }

    [JsonPropertyName("sample_aspect_ratio")]
    public string? SampleAspectRatio { get; set; }

    [JsonPropertyName("display_aspect_ratio")]
    public string? DisplayAspectRatio { get; set; }

    [JsonPropertyName("pix_fmt")]
    public string? PixelFormat { get; set; }

    [JsonPropertyName("level")]
    public int? Level { get; set; }

    [JsonPropertyName("color_range")]
    public string? ColorRange { get; set; }

    [JsonPropertyName("color_space")]
    public string? ColorSpace { get; set; }

    [JsonPropertyName("color_transfer")]
    public string? ColorTransfer { get; set; }

    [JsonPropertyName("color_primaries")]
    public string? ColorPrimaries { get; set; }

    [JsonPropertyName("refs")]
    public int? Refs { get; set; }

    [JsonPropertyName("is_avc")]
    public string? IsAvc { get; set; }

    [JsonPropertyName("nal_length_size")]
    public string? NalLengthSize { get; set; }

    [JsonPropertyName("r_frame_rate")]
    public string? RealFrameRate { get; set; }

    [JsonPropertyName("avg_frame_rate")]
    public string? AverageFrameRate { get; set; }

    [JsonPropertyName("bit_rate")]
    public string? BitRate { get; set; }

    [JsonPropertyName("bits_per_raw_sample")]
    public string? BitsPerRawSample { get; set; }

    [JsonPropertyName("nb_frames")]
    public string? NumberOfFrames { get; set; }

    [JsonPropertyName("sample_fmt")]
    public string? SampleFormat { get; set; }

    [JsonPropertyName("sample_rate")]
    public string? SampleRate { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }

    [JsonPropertyName("channel_layout")]
    public string? ChannelLayout { get; set; }

    [JsonPropertyName("bits_per_sample")]
    public int? BitsPerSample { get; set; }

    [JsonPropertyName("duration_ts")]
    public long? DurationTs { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("disposition")]
    public Dictionary<string, int>? Disposition { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }
}
