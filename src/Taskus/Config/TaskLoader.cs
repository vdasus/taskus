using System.IO.Abstractions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Taskus.Config;

/// <summary>Task file → secrets resolved → bound to <see cref="TaskOptions"/> → validated.</summary>
public static partial class TaskLoader
{
    public static Result<TaskOptions> Load(IFileSystem fs, string file, JsonObject secrets) =>
        YamlJson.Parse(fs.File.ReadAllText(file))
            .Bind(tree => Secrets.Resolve(tree, secrets))
            .Bind(tree => tree is JsonObject root ? Result.Success(root) : Result.Failure<JsonObject>("must be a mapping"))
            .Bind(root => CheckType(root, "source", typeof(SourceOptions)).Bind(() => CheckType(root, "sink", typeof(SinkOptions))).Map(() => root))
            .Bind(Bind)
            .Map(task => task with { Name = task.Name ?? fs.Path.GetFileNameWithoutExtension(file) })
            .Bind(TaskValidation.Validate)
            .MapError(error => $"{file}: {error}");

    // A missing or unknown "type" would otherwise surface as "abstract types are not supported".
    private static Result CheckType(JsonObject root, string section, Type baseType)
    {
        var types = baseType.GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!).ToList();
        if (root[section] is not JsonObject settings)
            return root.ContainsKey(section) ? Result.Failure($"{section}: must be a mapping") : Result.Success(); // missing: binding reports it
        var type = settings["type"]?.GetValue<string>().ToLowerInvariant();
        if (type is null || !types.Contains(type))
            return Result.Failure($"{section}.type: {(type is null ? "is required" : $"unknown type '{type}'")} ({string.Join(" | ", types)})");
        settings["type"] = type; // the discriminator is matched case-sensitively
        if (settings["method"] is JsonValue method && method.TryGetValue<string>(out var name))
            settings["method"] = name.ToUpperInvariant(); // so are the GET | POST ... enum names
        return Result.Success();
    }

    private static Result<TaskOptions> Bind(JsonObject root)
    {
        try
        {
            return root.Deserialize<TaskOptions>(TaskJson.Read)!;
        }
        catch (JsonException ex)
        {
            return Result.Failure<TaskOptions>(Describe(ex));
        }
    }

    [GeneratedRegex(@"The JSON property '(?<name>[^']+)' could not be mapped")]
    private static partial Regex Unmapped();

    [GeneratedRegex(@"missing required properties including: (?<names>.+?)\.?$")]
    private static partial Regex Missing();

    [GeneratedRegex(@"could not be converted to (?<type>[\w.]+?)\.?(?:\s|$)")]
    private static partial Regex Conversion();

    private static readonly Dictionary<string, string> Expected = new()
    {
        ["System.Int32"] = "a whole number",
        ["System.Double"] = "a number",
        ["System.Boolean"] = "true or false",
        ["System.String"] = "text",
        [typeof(ErrorPolicy).FullName!] = "stop | continue",
        [typeof(ReportWhen).FullName!] = "always | onError | never",
        [typeof(TransactionScope).FullName!] = "perRow | all",
        [typeof(HttpMethodName).FullName!] = "GET | POST | PUT | PATCH | DELETE",
    };

    // System.Text.Json messages name .NET types and JSON byte positions, which mean nothing in a YAML file.
    private static string Describe(JsonException ex)
    {
        var path = (ex.Path ?? "$").TrimStart('$').TrimStart('.');
        var message = ex.Message;
        var unmapped = Unmapped().Match(message);
        var missing = Missing().Match(message);
        var conversion = Conversion().Match(message);
        var text = unmapped.Success ? "unknown setting" // the path already ends with its name
            : missing.Success ? $"missing required setting {missing.Groups["names"].Value}"
            : conversion.Success ? $"must be {Expected.GetValueOrDefault(conversion.Groups["type"].Value, conversion.Groups["type"].Value)}"
            : message.Split(" Path:")[0];
        return path.Length == 0 ? text : $"{path}: {text}";
    }
}
