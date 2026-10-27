using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Taskus.Config;

/// <summary>Loads <c>secrets.yaml</c> and replaces <c>${secret:name}</c> references in a task tree.</summary>
public static partial class Secrets
{
    [GeneratedRegex(@"\$\{secret:([^}]+)\}")]
    private static partial Regex Reference();

    public static JsonObject None => [];

    /// <summary>A missing file gives no secrets: tasks without references still run.</summary>
    public static Result<JsonObject> Load(string file)
    {
        if (!File.Exists(file))
            return None;

        return YamlJson.Parse(File.ReadAllText(file)).MapError(error => $"{file}: {error}").Bind(tree => tree switch
        {
            null => Result.Success(None),
            JsonObject map => Result.Success(map),
            _ => Result.Failure<JsonObject>($"{file}: must be a mapping of name to value"),
        });
    }

    /// <summary>Returns a copy of <paramref name="node"/> with every reference replaced.</summary>
    public static Result<JsonNode?> Resolve(JsonNode? node, JsonObject secrets)
    {
        switch (node)
        {
            case JsonObject map:
                var resolvedMap = new JsonObject();
                foreach (var (key, value) in map)
                {
                    var resolved = Resolve(value, secrets);
                    if (resolved.IsFailure)
                        return resolved;
                    resolvedMap[key] = resolved.Value;
                }

                return resolvedMap;
            case JsonArray list:
                var resolvedList = new JsonArray();
                foreach (var item in list)
                {
                    var resolved = Resolve(item, secrets);
                    if (resolved.IsFailure)
                        return resolved;
                    resolvedList.Add(resolved.Value);
                }

                return resolvedList;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return ResolveText(text, secrets);
            default:
                return Result.Success(node?.DeepClone());
        }
    }

    // A value that is only a reference takes the secret as is, so a secret can be a whole mapping (smtp).
    // A reference inside longer text must point to a text secret.
    private static Result<JsonNode?> ResolveText(string text, JsonObject secrets)
    {
        var whole = Reference().Match(text);
        if (whole.Success && whole.Length == text.Length)
            return Lookup(whole.Groups[1].Value, secrets).Map(secret => secret?.DeepClone());

        string? error = null;
        var replaced = Reference().Replace(text, m =>
        {
            var secret = Lookup(m.Groups[1].Value, secrets);
            if (secret is { IsSuccess: true, Value: JsonValue value } && value.TryGetValue<string>(out var secretText))
                return secretText;
            error ??= secret.IsFailure ? secret.Error : $"secret '{m.Groups[1].Value}' is not text and cannot be part of a longer value";
            return m.Value;
        });

        return error is null ? JsonValue.Create(replaced) : Result.Failure<JsonNode?>(error);
    }

    private static Result<JsonNode?> Lookup(string name, JsonObject secrets) =>
        secrets.TryGetPropertyValue(name, out var value)
            ? Result.Success(value)
            : Result.Failure<JsonNode?>($"secret '{name}' is not defined in secrets.yaml");
}
