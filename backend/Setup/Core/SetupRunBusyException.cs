namespace NzbWebDAV.Setup.Core;

/// <summary>Safe retry signal when a durable setup run lease is held elsewhere.</summary>
public sealed class SetupRunBusyException : Exception
{
    public const string SafeCode = "setup-run-busy";
    public const string SafeMessage = "Setup is busy. Retry shortly.";
    public const int DefaultRetryAfterSeconds = 1;
    public const int MaximumRetryAfterSeconds = 30;

    public SetupRunBusyException(int retryAfterSeconds = DefaultRetryAfterSeconds)
        : base(SafeMessage)
    {
        RetryAfterSeconds = Math.Clamp(retryAfterSeconds, 1, MaximumRetryAfterSeconds);
    }

    public string Code => SafeCode;
    public int RetryAfterSeconds { get; }
}
