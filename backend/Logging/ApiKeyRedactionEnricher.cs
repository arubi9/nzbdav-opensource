using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Logging;

public partial class ApiKeyRedactionEnricher : ILogEventEnricher
{
    [GeneratedRegex(@"(apikey|api_key|api-key)=([^&\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var keysToRedact = new List<string>();

        foreach (var property in logEvent.Properties)
        {
            var rendered = property.Value.ToString();
            if (ApiKeyPattern().IsMatch(rendered))
                keysToRedact.Add(property.Key);
        }

        foreach (var key in keysToRedact)
        {
            var original = logEvent.Properties[key].ToString().Trim('"');
            var redacted = ApiKeyPattern().Replace(original, "$1=REDACTED");
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, redacted));
        }
    }
}
