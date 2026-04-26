using System.Text.RegularExpressions;
using System.Xml;

namespace NzbWebDAV.Models.Nzb;

public class NzbDocument
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore
    };

    public Dictionary<string, string> Metadata { get; } = new();

    public List<NzbFile> Files { get; } = [];

    public static async Task<NzbDocument> LoadAsync(Stream stream)
    {
        var document = new NzbDocument();
        using var reader = XmlReader.Create(stream, XmlSettings);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.Name)
            {
                case "head":
                    await ReadHeadAsync(reader, document.Metadata).ConfigureAwait(false);
                    break;
                case "file":
                    var file = await ReadFileAsync(reader).ConfigureAwait(false);
                    document.Files.Add(file);
                    break;
            }
        }

        return document;
    }

    private static async Task ReadHeadAsync(XmlReader reader, Dictionary<string, string> metadata)
    {
        if (reader.IsEmptyElement)
            return;

        while (true)
        {
            if (reader is { NodeType: XmlNodeType.EndElement, Name: "head" })
                break;

            if (reader is { NodeType: XmlNodeType.Element, Name: "meta" })
            {
                var type = reader.GetAttribute("type") ?? string.Empty;
                var value = await ReadElementContentTolerantAsync(reader).ConfigureAwait(false);
                metadata.Add(type, value);

                // helper advances the reader past EndElement - continue to check current position
                continue;
            }

            // Only read if we haven't processed an element that advanced us
            if (!await reader.ReadAsync().ConfigureAwait(false))
                break;
        }
    }

    private static async Task<NzbFile> ReadFileAsync(XmlReader reader)
    {
        var file = new NzbFile
        {
            Subject = reader.GetAttribute("subject") ?? string.Empty
        };

        if (reader.IsEmptyElement)
            return file;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader is { NodeType: XmlNodeType.EndElement, Name: "file" })
                break;

            if (reader is { NodeType: XmlNodeType.Element, Name: "segments" })
            {
                await ReadSegmentsAsync(reader, file).ConfigureAwait(false);
            }
        }

        return file;
    }

    // Some NZBs in the wild include nested elements inside <meta> or <segment>
    // (e.g. <segment number="1" bytes="383848">message-id<extra/></segment>).
    // ReadElementContentAsStringAsync throws "'Element' is an invalid XmlNodeType"
    // on mixed content. ReadInnerXmlAsync tolerates it; we strip any tags to
    // recover the text portion. Falls back to empty string on total parse failure
    // so a single bad element doesn't abort the whole NZB.
    private static readonly Regex TagStrippingRegex = new("<[^>]*>", RegexOptions.Compiled);

    private static async Task<string> ReadElementContentTolerantAsync(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync().ConfigureAwait(false);
            return string.Empty;
        }

        try
        {
            var inner = await reader.ReadInnerXmlAsync().ConfigureAwait(false);
            // Fast path: well-formed NZBs have plain text content
            if (inner.IndexOf('<') < 0) return inner.Trim();
            // Slow path: strip nested elements/comments, keep text only
            return TagStrippingRegex.Replace(inner, string.Empty).Trim();
        }
        catch (XmlException)
        {
            // Truly unrecoverable element — skip it and continue parsing the rest
            try { await reader.SkipAsync().ConfigureAwait(false); } catch { }
            return string.Empty;
        }
    }

    private static async Task ReadSegmentsAsync(XmlReader reader, NzbFile file)
    {
        if (reader.IsEmptyElement)
            return;

        while (true)
        {
            if (reader is { NodeType: XmlNodeType.EndElement, Name: "segments" })
                break;

            if (reader is { NodeType: XmlNodeType.Element, Name: "segment" })
            {
                var bytesAttr = reader.GetAttribute("bytes");
                var numberAttr = reader.GetAttribute("number");
                var segment = new NzbSegment
                {
                    Number = int.TryParse(numberAttr, out var number) ? number : 0,
                    Bytes = long.TryParse(bytesAttr, out var bytes) ? bytes : 0,
                    MessageId = await ReadElementContentTolerantAsync(reader).ConfigureAwait(false)
                };
                file.Segments.Add(segment);

                // helper advances the reader past EndElement - continue to check current position
                continue;
            }

            // Only read if we haven't processed an element that advanced us
            if (!await reader.ReadAsync().ConfigureAwait(false))
                break;
        }
    }
}
