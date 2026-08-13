using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace NzbWebDAV.Api.Controllers.Setup;

public enum SetupGrantOperation
{
    Handoff,
    Renew,
    Recovery,
    Repair,
}

public sealed class SetupGrantRequest
{
    private const string InvalidPayloadError = "Invalid setup credentials payload.";
    private const int DefaultFieldCount = 2;
    private const int DefaultUsernameLength = 255;
    private const int DefaultPasswordLength = 1024;

    public const long DefaultMaxRequestBodySizeBytes = 4 * 1024;

    internal sealed record Limits(long MaxBodyBytes, int MaxFieldCount, int MaxUsernameLength, int MaxPasswordLength);

    internal static readonly Limits HandoffLimits = new(DefaultMaxRequestBodySizeBytes, DefaultFieldCount, DefaultUsernameLength, DefaultPasswordLength);
    internal static readonly Limits RenewLimits = new(DefaultMaxRequestBodySizeBytes, DefaultFieldCount, DefaultUsernameLength, DefaultPasswordLength);
    internal static Func<ArrayPool<byte>> BufferPoolFactory = () => ArrayPool<byte>.Shared;

    /// <summary>
    /// Production parser entry point used by every setup credential operation.
    /// Keep this as a single boundary so captured frontend bytes can be tested
    /// without replacing the parser with a controller mock.
    /// </summary>
    public static Task<SetupGrantRequest> ParseAsync(HttpContext context, CancellationToken cancellationToken)
        => ParseAsync(context, SetupGrantOperation.Handoff, cancellationToken);

    public static Task<SetupGrantRequest> ParseAsync(
        HttpContext context,
        SetupGrantOperation operation,
        CancellationToken cancellationToken)
        => ReadAsync(context, operation switch
        {
            SetupGrantOperation.Handoff => HandoffLimits,
            SetupGrantOperation.Renew or SetupGrantOperation.Recovery or SetupGrantOperation.Repair => RenewLimits,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        }, cancellationToken);

    public string Username { get; init; }
    public string Password { get; init; }

    private SetupGrantRequest(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public static Task<SetupGrantRequest> ReadAsync(HttpContext context, CancellationToken cancellationToken)
        => ReadAsync(context, HandoffLimits, cancellationToken);

    public static Task<SetupGrantRequest> ReadHandoffAsync(HttpContext context, CancellationToken cancellationToken)
        => ParseAsync(context, SetupGrantOperation.Handoff, cancellationToken);

    public static Task<SetupGrantRequest> ReadRenewAsync(HttpContext context, CancellationToken cancellationToken)
        => ParseAsync(context, SetupGrantOperation.Renew, cancellationToken);

    public static Task<SetupGrantRequest> ReadRecoveryAsync(HttpContext context, CancellationToken cancellationToken)
        => ParseAsync(context, SetupGrantOperation.Recovery, cancellationToken);

    public static Task<SetupGrantRequest> ReadRepairAsync(HttpContext context, CancellationToken cancellationToken)
        => ParseAsync(context, SetupGrantOperation.Repair, cancellationToken);

    internal static async Task<SetupGrantRequest> ReadAsync(HttpContext context, Limits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var request = context.Request;
        if (!IsFormContentType(request.ContentType))
            throw new BadHttpRequestException("Invalid setup credentials content type.");

        if (request.ContentLength.HasValue && request.ContentLength > limits.MaxBodyBytes)
            throw new BadHttpRequestException("Setup credentials payload is too large.");

        var body = await ReadBodyAsync(request.Body, limits.MaxBodyBytes, request.ContentLength, cancellationToken)
            .ConfigureAwait(false);

        var formValues = ParseForm(body, limits.MaxFieldCount);

        if (!TryGetSingleValue(formValues, "username", out var username)
            || !TryGetSingleValue(formValues, "password", out var password))
        {
            throw new BadHttpRequestException(InvalidPayloadError);
        }

        if (string.IsNullOrWhiteSpace(username) || username.Length > limits.MaxUsernameLength)
            throw new BadHttpRequestException(InvalidPayloadError);

        if (string.IsNullOrWhiteSpace(password) || password.Length > limits.MaxPasswordLength)
            throw new BadHttpRequestException(InvalidPayloadError);

        return new SetupGrantRequest(username.ToLowerInvariant(), password);
    }

    private static bool IsFormContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
            return false;

        return parsedContentType.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
               && (!parsedContentType.Charset.HasValue
                   || parsedContentType.Charset.Value.Equals("UTF-8", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadBodyAsync(
        Stream body,
        long maxBytes,
        long? declaredLength,
        CancellationToken cancellationToken)
    {
        var pool = BufferPoolFactory();
        var buffer = pool.Rent(4096);
        var collected = pool.Rent((int)Math.Min(maxBytes, 8192));
        var totalBytes = 0;

        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (totalBytes + read > maxBytes)
                    throw new BadHttpRequestException("Setup credentials payload is too large.");

                if (collected.Length < totalBytes + read)
                {
                    var grown = pool.Rent(Math.Max(collected.Length * 2, totalBytes + read));
                    Buffer.BlockCopy(collected, 0, grown, 0, totalBytes);
                    Array.Clear(collected, 0, totalBytes);
                    pool.Return(collected, true);
                    collected = grown;
                }

                Buffer.BlockCopy(buffer, 0, collected, totalBytes, read);
                totalBytes += read;
            }

            if (declaredLength.HasValue && declaredLength.Value != totalBytes)
                throw new BadHttpRequestException(InvalidPayloadError);

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(collected, 0, totalBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BadHttpRequestException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BadHttpRequestException(InvalidPayloadError);
        }
        finally
        {
            pool.Return(buffer, true);
            pool.Return(collected, true);
        }
    }

    private static Dictionary<string, string[]> ParseForm(string body, int maxFieldCount)
    {
        Dictionary<string, string[]> formValues;

        try
        {
            if (!HasValidPercentEscapes(body))
                throw new BadHttpRequestException(InvalidPayloadError);

            var parsed = QueryHelpers.ParseQuery(body);
            if (parsed.Count > maxFieldCount
                || parsed.Keys.Any(key => key is not ("username" or "password")))
            {
                throw new BadHttpRequestException(InvalidPayloadError);
            }

            formValues = parsed
                .Where(pair => pair.Key is "username" or "password")
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(value => value ?? string.Empty).ToArray());
        }
        catch (BadHttpRequestException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BadHttpRequestException(InvalidPayloadError);
        }

        if (formValues.Count != maxFieldCount)
            throw new BadHttpRequestException(InvalidPayloadError);

        if (formValues.Any(pair => (pair.Key == "username" || pair.Key == "password") && pair.Value.Length != 1))
            throw new BadHttpRequestException(InvalidPayloadError);

        return formValues;
    }

    private static bool HasValidPercentEscapes(string body)
    {
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] != '%')
                continue;

            if (index + 2 >= body.Length
                || !Uri.IsHexDigit(body[index + 1])
                || !Uri.IsHexDigit(body[index + 2]))
                return false;

            index += 2;
        }

        return true;
    }

    private static bool TryGetSingleValue(Dictionary<string, string[]> values, string key, out string value)
    {
        value = string.Empty;

        if (!values.TryGetValue(key, out var valuesForKey) || valuesForKey.Length != 1)
            return false;

        value = valuesForKey[0];
        return true;
    }
}
