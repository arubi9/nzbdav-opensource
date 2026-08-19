using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// The single semantic parser for provider probe JSON. Both the HTTP client and
/// the Jellyfin provider must apply the same duplicate-name and DTO rules; a
/// root-only shape check is not sufficient because ffprobe contains nested
/// objects such as tags and disposition.
/// </summary>
internal static class FfprobeJsonParser
{
    private const int MaxArrayItems = 50_000;
    private const int MaxObjectProperties = 256;
    private const int MaxStringBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    internal static FfprobeOutput Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = 32,
            CommentHandling = JsonCommentHandling.Disallow
        });
        RejectDuplicateProperties(document.RootElement);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(document.RootElement, "format", out var format)
            || format.ValueKind != JsonValueKind.Object
            || !TryGetProperty(format, "format_name", out var formatName)
            || formatName.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(formatName.GetString())
            || !TryGetProperty(document.RootElement, "streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array
            || streams.EnumerateArray().Any(stream => stream.ValueKind != JsonValueKind.Object))
            throw new JsonException("Probe response did not match the provider media-info contract.");

        var result = JsonSerializer.Deserialize<FfprobeOutput>(bytes, Options)
            ?? throw new JsonException("Probe response was null.");
        if (result.Format is null || string.IsNullOrWhiteSpace(result.Format.FormatName)
            || result.Streams is null)
            throw new JsonException("Probe response did not contain required provider fields.");
        return result;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        var arrays = 0;
        var properties = 0;
        var strings = 0L;
        var stack = new Stack<(JsonElement Element, bool IsTagMap)>();
        stack.Push((element, false));
        while (stack.Count > 0)
        {
            var (current, isTagMap) = stack.Pop();
            switch (current.ValueKind)
            {
                case JsonValueKind.String:
                    if (++strings > MaxStringBytes
                        || Encoding.UTF8.GetByteCount(current.GetString() ?? string.Empty) > MaxStringBytes)
                        throw new JsonException("Probe response contains oversized strings.");
                    break;
                case JsonValueKind.Array:
                    if (++arrays > MaxArrayItems || current.GetArrayLength() > MaxArrayItems)
                        throw new JsonException("Probe response contains too many array items.");
                    foreach (var child in current.EnumerateArray())
                        stack.Push((child, false));
                    break;
                case JsonValueKind.Object:
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in current.EnumerateObject())
                    {
                        if (!isTagMap && !names.Add(property.Name))
                            throw new JsonException($"Probe response contains duplicate property '{property.Name}'.");
                        if (++properties > MaxObjectProperties * MaxArrayItems)
                            throw new JsonException("Probe response contains too many object properties.");
                        stack.Push((property.Value,
                            string.Equals(property.Name, "tags", StringComparison.OrdinalIgnoreCase)));
                    }
                    break;
            }
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
