namespace Taskus.Mapping;

/// <summary>Turns a source row into a sink row. Each target field is a template over <c>row</c>.</summary>
public sealed class RowMapper
{
    private readonly IReadOnlyList<(string Field, TaskTemplate Template)> fields;

    private RowMapper(IReadOnlyList<(string Field, TaskTemplate Template)> fields) => this.fields = fields;

    /// <summary>An empty map passes source rows through unchanged.</summary>
    public static Result<RowMapper> Create(IEnumerable<KeyValuePair<string, string>> map)
    {
        var fields = new List<(string, TaskTemplate)>();
        var errors = new List<string>();
        foreach (var (field, text) in map)
        {
            TaskTemplate.Parse(text)
                .Tap(t => fields.Add((field, t)))
                .TapError(error => errors.Add($"map.{field}: {error}"));
        }

        return errors.Count == 0 ? new RowMapper(fields) : Result.Failure<RowMapper>(string.Join("; ", errors));
    }

    public Result<Row> Map(Row row)
    {
        if (fields.Count == 0)
            return Result.Success(row);

        var variables = new Dictionary<string, object?> { ["row"] = row };
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, template) in fields)
        {
            var value = template.Evaluate($"map.{field}", variables);
            if (value.IsFailure)
                return Result.Failure<Row>(value.Error);
            result[field] = value.Value;
        }

        return result;
    }
}
