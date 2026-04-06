namespace NzbWebDAV.Models;

public readonly record struct StreamingBufferSettings
{
    public int MaxBufferedSegments { get; init; }
    public int StartupBufferedSegments { get; init; }
    public int RampAfterConsumedSegments { get; init; }

    public StreamingBufferSettings(int maxBufferedSegments, int startupBufferedSegments, int rampAfterConsumedSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBufferedSegments);
        ArgumentOutOfRangeException.ThrowIfNegative(startupBufferedSegments);
        ArgumentOutOfRangeException.ThrowIfNegative(rampAfterConsumedSegments);

        if (startupBufferedSegments > maxBufferedSegments)
            throw new ArgumentOutOfRangeException(
                nameof(startupBufferedSegments),
                "StartupBufferedSegments cannot exceed MaxBufferedSegments."
            );

        if (maxBufferedSegments > 0 && startupBufferedSegments == 0)
            throw new ArgumentOutOfRangeException(
                nameof(startupBufferedSegments),
                "StartupBufferedSegments must be greater than zero when buffering is enabled."
            );

        MaxBufferedSegments = maxBufferedSegments;
        StartupBufferedSegments = startupBufferedSegments;
        RampAfterConsumedSegments = rampAfterConsumedSegments;
    }

    public static StreamingBufferSettings Fixed(int articleBufferSize) =>
        new(
            maxBufferedSegments: articleBufferSize,
            startupBufferedSegments: articleBufferSize,
            rampAfterConsumedSegments: 0
        );

    public static StreamingBufferSettings LiveDefault(int articleBufferSize) =>
        new(
            maxBufferedSegments: articleBufferSize,
            startupBufferedSegments: Math.Min(2, articleBufferSize),
            rampAfterConsumedSegments: 2
        );
}
