namespace NzbWebDAV.Api.Controllers;

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException() : base("The settings were changed in another tab.")
    {
    }
}
