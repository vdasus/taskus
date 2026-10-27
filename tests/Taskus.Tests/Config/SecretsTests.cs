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
        var smtp = new Dictionary<object, object?> { ["server"] = "mail.local" };
        var secrets = new Dictionary<string, object?> { ["smtp_main"] = smtp };
        var tree = new Dictionary<object, object?> { ["smtp"] = "${secret:smtp_main}" };

        var result = Secrets.Resolve(tree, secrets);

        result.IsSuccess.Should().BeTrue();
        ((IDictionary<object, object?>)result.Value!)["smtp"].Should().BeSameAs(smtp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_ReferenceInsideText_ReplacesItInNestedValues()
    {
        var password = fixture.Create<string>();
        var secrets = new Dictionary<string, object?> { ["pg"] = password };
        var tree = new Dictionary<object, object?>
        {
            ["sink"] = new Dictionary<object, object?> { ["connection"] = "Host=db;Password=${secret:pg};" },
        };

        var result = Secrets.Resolve(tree, secrets);

        var sink = (IDictionary<object, object?>)((IDictionary<object, object?>)result.Value!)["sink"]!;
        sink["connection"].Should().Be($"Host=db;Password={password};");
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
        var secrets = new Dictionary<string, object?> { ["smtp"] = new Dictionary<object, object?>() };

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
        result.Value["pg_main"].Should().Be("Host=db");
        result.Value["smtp_main"].Should().BeAssignableTo<IDictionary<object, object?>>();
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
