using System.ComponentModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Taskus.Config;

/// <summary>
/// JSON Schema of task files, generated from <see cref="TaskOptions"/>. Editors with a YAML language server use it
/// for completion and checks: <c># yaml-language-server: $schema=...</c> on the first line of a task file.
/// </summary>
public static class TaskSchema
{
    public const string Url = "https://raw.githubusercontent.com/vdasus/taskus/main/schema/task.schema.json";

    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps quotes and non-ASCII readable in descriptions
    };

    public static string Generate()
    {
        var schema = TaskJson.Schema.GetJsonSchemaAsNode(typeof(TaskOptions), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Describe,
        }).AsObject();

        var root = new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["$id"] = Url, ["title"] = "Taskus task" };
        foreach (var (key, value) in schema.ToList())
        {
            schema.Remove(key);
            root[key] = value;
        }

        return root.ToJsonString(Output).ReplaceLineEndings("\n") + "\n";
    }

    private static JsonNode Describe(JsonSchemaExporterContext context, JsonNode schema)
    {
        var description = context.PropertyInfo is { } property
            ? property.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true).OfType<DescriptionAttribute>().FirstOrDefault()
            : context.TypeInfo.Type.GetCustomAttribute<DescriptionAttribute>();
        if (description is not null && schema is JsonObject node && !node.ContainsKey("description"))
            node.Insert(0, "description", description.Description);
        return schema;
    }
}
