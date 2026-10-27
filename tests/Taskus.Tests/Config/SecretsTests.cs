using System.Text.Json.Nodes;
using AutoFixture;
using FluentAssertions;
using Taskus.Config;

namespace Taskus.Tests.Config;

public class SecretsTests
{
    private readonly Fixture fixture = new();

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_WholeValueReference_TakesSecretAsIs()
    {
        var secrets = new JsonObject { ["smtp_main"] = new JsonObject { ["server"] = "mail.local" } };
        var tree = new JsonObject { ["smtp"] = "${secret:smtp_main}" };

        var result = Secrets.Resolve(tree, secrets);

        result.IsSuccess.Should().BeTrue();
        result.Value!["smtp"]!["server"]!.GetValue<string>().Should().Be("mail.local");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_ReferenceInsideText_ReplacesItInNestedValues()
    {
        var password = fixture.Create<string>();
        var secrets = new JsonObject { ["pg"] = password };
        var tree = new JsonObject
        {
            ["sink"] = new JsonObject { ["connection"] = "Host=db;Password=${secret:pg};" },
        };

        var result = Secrets.Resolve(tree, secrets);

        result.Value!["sink"]!["connection"]!.GetValue<string>().Should().Be($"Host=db;Password={password};");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_UnknownSecret_FailsWithItsName()
    {
        var result = Secrets.Resolve("x ${secret:nope} y", Secrets.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("'nope'");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_MappingSecretInsideText_Fails()
    {
        var secrets = new JsonObject { ["smtp"] = new JsonObject() };

        var result = Secrets.Resolve("server=${secret:smtp}", secrets);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not text");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_ReadsYamlMapping()
    {
        using var temp = new TempFolder();
        var file = temp.Write("secrets.yaml", "pg_main: Host=db\nsmtp_main:\n  server: mail.local\n");

        var result = Secrets.Load(file);

        result.IsSuccess.Should().BeTrue();
        result.Value["pg_main"]!.GetValue<string>().Should().Be("Host=db");
        result.Value["smtp_main"]!["server"]!.GetValue<string>().Should().Be("mail.local");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_MissingFile_GivesNoSecrets()
    {
        var result = Secrets.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
