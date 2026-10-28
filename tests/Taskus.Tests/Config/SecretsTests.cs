using System.Text.Json.Nodes;
using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
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
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { ["secrets.yaml"] = new("pg_main: Host=db\nsmtp_main:\n  server: mail.local\n") });

        var result = Secrets.Load(fs, "secrets.yaml");

        result.IsSuccess.Should().BeTrue();
        result.Value["pg_main"]!.GetValue<string>().Should().Be("Host=db");
        result.Value["smtp_main"]!["server"]!.GetValue<string>().Should().Be("mail.local");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_MissingFile_GivesNoSecrets()
    {
        var result = Secrets.Load(new MockFileSystem(), "secrets.yaml");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    // Stands in for DPAPI on every OS; tests of the real one are in SecretsProtectionTests.
    internal static byte[] Flip(byte[] data) => data.Select(b => (byte)~b).ToArray();

    private static MockFileSystem Files(params string[] names) =>
        new(names.ToDictionary(n => n, n => n.EndsWith(".sec") ? new MockFileData(Flip("pg: from-sec\n"u8.ToArray())) : new MockFileData("pg: from-yaml\n")));

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Load_Protected_ReadsOnlyTheProtectedFile()
    {
        var result = Secrets.Load(Files("secrets.sec"), "secrets.yaml", Flip);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value["pg"]!.GetValue<string>().Should().Be("from-sec");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Load_ProtectedButOnlyPlainFile_FailsAskingToProtect()
    {
        var result = Secrets.Load(Files("secrets.yaml"), "secrets.yaml", Flip);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("secrets.yaml is not protected. Run 'taskus secrets protect' to replace it with secrets.sec");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Load_ProtectedAndBothFiles_FailsAskingToDeletePlainFile()
    {
        var result = Secrets.Load(Files("secrets.yaml", "secrets.sec"), "secrets.yaml", Flip);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith("Both secrets.sec and secrets.yaml exist. Delete the unprotected secrets.yaml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_ProtectedFileFromAnotherMachine_FailsWithHint()
    {
        var result = Secrets.Load(Files("secrets.sec"), "secrets.yaml", _ => throw new CryptographicException());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("cannot be decrypted").And.Contain("another machine");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_ProtectedAndNoFile_GivesNoSecrets()
    {
        var result = Secrets.Load(new MockFileSystem(), "secrets.yaml", Flip);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_WithoutProtectionAndBothFiles_ReadsPlainFile()
    {
        var result = Secrets.Load(Files("secrets.yaml", "secrets.sec"), "secrets.yaml");

        result.Value["pg"]!.GetValue<string>().Should().Be("from-yaml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Load_WithoutProtectionAndOnlyProtectedFile_FailsNamingWindows()
    {
        var result = Secrets.Load(Files("secrets.sec"), "secrets.yaml");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("only on Windows");
    }
}
