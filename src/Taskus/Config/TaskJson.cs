using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Taskus.Config;

/// <summary>JSON settings for binding task files and for exporting their schema.</summary>
internal static class TaskJson
{
    /// <summary>
    /// Binding: YAML scalars arrive as text, so numbers and booleans are read from strings.
    /// Unknown keys are errors, which catches typos such as <c>procesed:</c>.
    /// </summary>
    public static readonly JsonSerializerOptions Read = Create(lenient: true);

    /// <summary>Schema export: the same model without the text-to-number leniency, so the schema stays strict.</summary>
    public static readonly JsonSerializerOptions Schema = Create(lenient: false);

    private static JsonSerializerOptions Create(bool lenient)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true, // "type" need not be the first key
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        if (lenient)
        {
            options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
            options.Converters.Add(new TextBoolConverter());
        }

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class TextBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
            _ => throw new JsonException(),
        };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }
}

/// <summary>
/// Reads YAML into a JSON tree. Every scalar stays text (a plain empty value, <c>~</c> or <c>null</c> is null), so
/// <c>sheet: 2024.10</c> or <c>code: 007</c> keep their exact spelling; binding converts text to numbers where the
/// model wants a number.
/// </summary>
internal static class YamlJson
{
    public static Result<JsonNode?> Parse(string text)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            return stream.Documents.Count == 0 ? Result.Success<JsonNode?>(null) : Convert(stream.Documents[0].RootNode);
        }
        catch (YamlException ex)
        {
            return Result.Failure<JsonNode?>(ex.Message);
        }
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value ?? "", Convert(kv.Value)))),
        YamlSequenceNode list => new JsonArray(list.Children.Select(Convert).ToArray()),
        YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "~" or "null" or "Null" or "NULL" } => null,
        YamlScalarNode scalar => JsonValue.Create(scalar.Value ?? ""),
        _ => throw new YamlException($"unsupported YAML node {node.NodeType}"),
    };
}
