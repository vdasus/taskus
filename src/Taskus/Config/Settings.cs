using System.Globalization;

namespace Taskus.Config;

/// <summary>One mapping of a task file, after secrets are resolved. Keys are case-insensitive.</summary>
public sealed class Settings
{
    private readonly Dictionary<string, object?> values;

    private Settings(string path, Dictionary<string, object?> values)
    {
        Path = path;
        this.values = values;
    }

    /// <summary>Location in the task file, for error messages, e.g. <c>sink</c>.</summary>
    public string Path { get; }

    public static Result<Settings> From(string path, object? node) => node switch
    {
        IDictionary<object, object?> map => new Settings(path, map.ToDictionary(
            kv => Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!, kv => kv.Value, StringComparer.OrdinalIgnoreCase)),
        _ => Result.Failure<Settings>($"{(path.Length == 0 ? "task" : path)} must be a mapping"),
    };

    public static Settings Of(string path, IDictionary<string, object?> values) =>
        new(path, new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase));

    public string? Optional(string key) => values.GetValueOrDefault(key) as string;

    public Result<string> Required(string key) =>
        Optional(key) is { Length: > 0 } value ? value : Fail<string>(key, "is required");

    public Result<int> Int(string key, int fallback) => Optional(key) switch
    {
        null => fallback,
        var s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        var s => Fail<int>(key, $"'{s}' is not a whole number"),
    };

    public Result<double> Double(string key, double fallback) => Optional(key) switch
    {
        null => fallback,
        var s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        var s => Fail<double>(key, $"'{s}' is not a number"),
    };

    public Result<bool> Bool(string key, bool fallback) => Optional(key) switch
    {
        null => fallback,
        var s when bool.TryParse(s, out var b) => b,
        var s => Fail<bool>(key, $"'{s}' must be true or false"),
    };

    public Result<T> Enum<T>(string key, T fallback) where T : struct, Enum => Optional(key) switch
    {
        null => fallback,
        var s when System.Enum.TryParse<T>(s, ignoreCase: true, out var e) && System.Enum.IsDefined(e) => e,
        var s => Fail<T>(key, $"'{s}' must be one of: {string.Join(" | ", System.Enum.GetNames<T>().Select(CamelCase))}"),
    };

    public Result<Settings> Section(string key) =>
        values.GetValueOrDefault(key) is { } node ? From(Join(key), node) : Fail<Settings>(key, "is required");

    public Result<Settings?> OptionalSection(string key) =>
        values.GetValueOrDefault(key) is { } node ? From(Join(key), node).Map(s => (Settings?)s) : Result.Success<Settings?>(null);

    /// <summary>A mapping whose values are all strings, in file order. Missing key gives an empty map.</summary>
    public Result<IReadOnlyList<KeyValuePair<string, string>>> StringMap(string key)
    {
        if (values.GetValueOrDefault(key) is not { } node)
            return Array.Empty<KeyValuePair<string, string>>();
        if (node is not IDictionary<object, object?> map)
            return Fail<IReadOnlyList<KeyValuePair<string, string>>>(key, "must be a mapping");

        var result = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in map)
        {
            if (v is not string text)
                return Fail<IReadOnlyList<KeyValuePair<string, string>>>($"{key}.{k}", "must be a text value");
            result.Add(KeyValuePair.Create(Convert.ToString(k, CultureInfo.InvariantCulture)!, text));
        }

        return result;
    }

    /// <summary>A list of text values. Missing key gives an empty list.</summary>
    public Result<IReadOnlyList<string>> StringList(string key) => values.GetValueOrDefault(key) switch
    {
        null => Array.Empty<string>(),
        IList<object?> list when list.All(item => item is string) => list.Cast<string>().ToList(),
        _ => Fail<IReadOnlyList<string>>(key, "must be a list of text values"),
    };

    private string Join(string key) => Path.Length == 0 ? key : $"{Path}.{key}";

    private Result<T> Fail<T>(string key, string message) => Result.Failure<T>($"{Join(key)} {message}");

    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
