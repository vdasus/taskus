using System.Text.RegularExpressions;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Taskus.Mapping;

/// <summary>A Scriban template from a task file.</summary>
/// <remarks>
/// A value that is a single <c>{{ expression }}</c> keeps the expression's type (number, date, null),
/// so a database sink gets typed values. Any other value renders to text.
/// </remarks>
public sealed partial class TaskTemplate
{
    private readonly Template template;
    private readonly bool isExpression;

    private TaskTemplate(Template template, bool isExpression)
    {
        this.template = template;
        this.isExpression = isExpression;
    }

    [GeneratedRegex(@"^\s*\{\{(?<e>(?:(?!\{\{|\}\}).)*)\}\}\s*$", RegexOptions.Singleline)]
    private static partial Regex SingleExpression();

    public static Result<TaskTemplate> Parse(string where, string text)
    {
        var expression = SingleExpression().Match(text);
        var template = expression.Success
            ? Template.Parse(expression.Groups["e"].Value, lexerOptions: new LexerOptions { Mode = ScriptMode.ScriptOnly })
            : Template.Parse(text);

        return template.HasErrors
            ? Result.Failure<TaskTemplate>($"{where}: {string.Join("; ", template.Messages)}")
            : new TaskTemplate(template, expression.Success);
    }

    /// <summary>Evaluates with the given top-level variables, e.g. <c>row</c>.</summary>
    public Result<object?> Evaluate(string where, IReadOnlyDictionary<string, object?> variables)
    {
        var globals = new ScriptObject();
        foreach (var (name, value) in variables)
            globals[name] = value is Row row ? ToScript(row) : value;

        var context = new TemplateContext { MemberRenamer = member => member.Name };
        context.PushGlobal(globals);
        try
        {
            return isExpression ? template.Evaluate(context) : template.Render(context);
        }
        catch (ScriptRuntimeException ex)
        {
            return Result.Failure<object?>($"{where}: {ex.OriginalMessage}");
        }
    }

    private static ScriptObject ToScript(Row row)
    {
        var result = new ScriptObject();
        foreach (var (key, value) in row)
            result[key] = value;
        return result;
    }
}
