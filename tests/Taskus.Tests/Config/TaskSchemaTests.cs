using System.IO.Abstractions;
using FluentAssertions;
using Taskus.Config;

namespace Taskus.Tests.Config;

public class TaskSchemaTests
{
    // examples/ and schema/ are copied next to the tests by the test project.
    private static string RepoRoot() => AppContext.BaseDirectory;

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void CommittedSchema_MatchesTheModel()
    {
        var committed = File.ReadAllText(Path.Combine(RepoRoot(), "schema", "task.schema.json")).ReplaceLineEndings("\n");

        committed.Should().Be(TaskSchema.Generate(), "run 'taskus schema > schema/task.schema.json' after changing TaskOptions");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Schema_RejectsUnknownKeysAndNamesSourceTypes()
    {
        var schema = TaskSchema.Generate();

        schema.Should().Contain("\"additionalProperties\": false").And.Contain("\"const\": \"excel\"").And.Contain("\"const\": \"http\"");
    }

    public static TheoryData<string> Examples() =>
        [.. Directory.GetFiles(Path.Combine(RepoRoot(), "examples"), "*.yaml").Select(Path.GetFileName)!];

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(Examples))]
    public void Example_LoadsWithExampleSecrets(string name)
    {
        var examples = Path.Combine(RepoRoot(), "examples");
        var fs = new FileSystem();
        var secrets = Secrets.Load(fs, Path.Combine(examples, "secrets.yaml.example")).Value;

        var result = TaskLoader.Load(fs, Path.Combine(examples, name), secrets);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
    }
}
