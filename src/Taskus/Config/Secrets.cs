using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Taskus.Config;

/// <summary>Loads <c>secrets.yaml</c> and replaces <c>${secret:name}</c> references in a task tree.</summary>
public static partial class Secrets
{
    public static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    [GeneratedRegex(@"\$\{secret:([^}]+)\}")]
    private static partial Regex Reference();

    /// <summary>A missing file gives no secrets: tasks without references still run.</summary>
    public static Result<IReadOnlyDictionary<string, object?>> Load(string file)
    {
        if (!File.Exists(file))
            return Result.Success(None);

        object? tree;
        try
        {
            tree = Yaml.Deserialize(File.ReadAllText(file));
        }
        catch (YamlException ex)
        {
            return Result.Failure<IReadOnlyDictionary<string, object?>>($"{file}: {ex.Message}");
        }

        return tree switch
        {
            null => Result.Success(None),
            IDictionary<object, object?> map => Result.Success<IReadOnlyDictionary<string, object?>>(
                map.ToDictionary(kv => kv.Key.ToString()!, kv => kv.Value)),
            _ => Result.Failure<IReadOnlyDictionary<string, object?>>($"{file}: must be a mapping of name to value"),
        };
    }

    public static Result<object?> Resolve(object? node, IReadOnlyDictionary<string, object?> secrets)
    {
        switch (node)
        {
            case string text:
                return ResolveText(text, secrets);
            case IDictionary<object, object?> map:
                var resolvedMap = new Dictionary<object, object?>();
                foreach (var (key, value) in map)
                {
                    var resolved = Resolve(value, secrets);
                    if (resolved.IsFailure)
                        return resolved;
                    resolvedMap[key] = resolved.Value;
                }

                return resolvedMap;
            case IList<object?> list:
                var resolvedList = new List<object?>();
                foreach (var item in list)
                {
                    var resolved = Resolve(item, secrets);
                    if (resolved.IsFailure)
                        return resolved;
                    resolvedList.Add(resolved.Value);
                }

                return resolvedList;
            default:
                return Result.Success(node);
        }
    }

    // A value that is only a reference takes the secret as is, so a secret can be a whole mapping (smtp).
    // A reference inside longer text must point to a text secret.
    private static Result<object?> ResolveText(string text, IReadOnlyDictionary<string, object?> secrets)
    {
        var whole = Reference().Match(text);
        if (whole.Success && whole.Length == text.Length)
            return Lookup(whole.Groups[1].Value, secrets);

        string? error = null;
        var replaced = Reference().Replace(text, m =>
        {
            var secret = Lookup(m.Groups[1].Value, secrets);
            if (secret is { IsSuccess: true, Value: string value })
                return value;
            error ??= secret.IsFailure ? secret.Error : $"secret '{m.Groups[1].Value}' is not text and cannot be part of a longer value";
            return m.Value;
        });

        return error is null ? replaced : Result.Failure<object?>(error);
    }

    private static Result<object?> Lookup(string name, IReadOnlyDictionary<string, object?> secrets) =>
        secrets.TryGetValue(name, out var value)
            ? Result.Success(value)
            : Result.Failure<object?>($"secret '{name}' is not defined in secrets.yaml");
}

internal static class Yaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    /// <summary>Untyped tree: mappings, lists and strings.</summary>
    public static object? Deserialize(string text) => Deserializer.Deserialize<object?>(text);
}
