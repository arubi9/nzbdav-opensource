namespace NzbWebDAV.Exceptions;

public class ServiceOverloadedException : Exception
{
    public ServiceOverloadedException()
        : base("Server is overloaded. Try again later.") { }
}
