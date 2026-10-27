using System.Text.Json.Nodes;
using Serilog.Core;
using Serilog.Events;

namespace Taskus;

/// <summary>
/// Replaces secret values with <c>***</c> in log events and report texts.
/// </summary>
/// <remarks>
/// Masked: every top-level text secret (connection strings, tokens), values of sensitive keys inside secret
/// mappings (<c>password</c>, <c>token</c>, ...), and <c>Password=</c> parts of connection strings.
/// Other values of a secret mapping, such as an SMTP server or port, stay readable. Values shorter than
/// four characters are not masked: they would hide ordinary numbers and words. Exception texts logged with a
/// stack trace are not masked.
/// </remarks>
public sealed class SecretMask : ILogEventEnricher
{
    public const string Mask = "***";
    private const int MinLength = 4;
    private static readonly string[] SensitiveKeys = ["password", "pwd", "token", "secret", "key"];

    private volatile string[] values = [];

    public void Add(JsonObject secrets)
    {
        var found = new HashSet<string>(values, StringComparer.Ordinal);
        foreach (var (name, value) in secrets)
            Collect(name, value, topLevel: true, found);
        values = found.OrderByDescending(v => v.Length).ToArray(); // longest first: a whole string before its parts
    }

    public string Apply(string text)
    {
        foreach (var value in values)
            text = text.Replace(value, Mask, StringComparison.Ordinal);
        return text;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (values.Length == 0)
            return;
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var masked = Apply(value);
            if (!ReferenceEquals(masked, value))
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, masked));
        }
    }

    private LogEventPropertyValue Apply(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } when Apply(text) is var masked && masked != text => new ScalarValue(masked),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(Apply)),
        StructureValue structure => new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Apply(p.Value))), structure.TypeTag),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(kv => KeyValuePair.Create(kv.Key, Apply(kv.Value)))),
        _ => value,
    };

    private static void Collect(string? key, JsonNode? node, bool topLevel, HashSet<string> found)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (topLevel || IsSensitive(key))
                    AddValue(text, found);
                foreach (var part in text.Split(';'))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length == 2 && IsSensitive(pair[0].Trim()))
                        AddValue(pair[1].Trim(), found);
                }

                break;
            case JsonObject map:
                foreach (var (k, v) in map)
                    Collect(k, v, topLevel: false, found);
                break;
            case JsonArray list:
                foreach (var item in list)
                    Collect(key, item, topLevel: false, found);
                break;
        }
    }

    private static bool IsSensitive(string? key) =>
        key is not null && SensitiveKeys.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static void AddValue(string value, HashSet<string> found)
    {
        if (value.Length >= MinLength)
            found.Add(value);
    }
}
