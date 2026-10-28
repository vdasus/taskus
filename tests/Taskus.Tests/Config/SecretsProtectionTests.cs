using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using FluentAssertions;
using Taskus.Config;

namespace Taskus.Tests.Config;

public class SecretsProtectionTests
{
    private const string Yaml = "pg: Host=db;Password=päss\n";

    private static Func<byte[], byte[]> Flip => SecretsTests.Flip;

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Protect_WritesProtectedFileAndDeletesPlainFile()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { ["secrets.yaml"] = new(Yaml) });

        var result = SecretsProtection.Protect(fs, "secrets.yaml", Flip, Flip);

        result.Value.Should().Be("secrets.sec");
        fs.File.Exists("secrets.yaml").Should().BeFalse();
        fs.File.ReadAllBytes("secrets.sec").Should().NotEqual(System.Text.Encoding.UTF8.GetBytes(Yaml));
        Secrets.Load(fs, "secrets.yaml", Flip).Value["pg"]!.GetValue<string>().Should().Be("Host=db;Password=päss");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Protect_InvalidYaml_KeepsPlainFileAndWritesNothing()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { ["secrets.yaml"] = new("- a\n- b\n") });

        var result = SecretsProtection.Protect(fs, "secrets.yaml", Flip, Flip);

        result.IsFailure.Should().BeTrue();
        fs.File.Exists("secrets.yaml").Should().BeTrue();
        fs.File.Exists("secrets.sec").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public void Protect_ProtectedFileDoesNotReadBack_KeepsPlainFile()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { ["secrets.yaml"] = new(Yaml) });

        var result = SecretsProtection.Protect(fs, "secrets.yaml", Flip, data => data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("secrets.yaml is kept");
        fs.File.ReadAllText("secrets.yaml").Should().Be(Yaml);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Unprotect_WritesPlainFileAndKeepsProtectedFile()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { ["secrets.yaml"] = new(Yaml) });
        SecretsProtection.Protect(fs, "secrets.yaml", Flip, Flip);

        var result = SecretsProtection.Unprotect(fs, "secrets.yaml", Flip);

        result.IsSuccess.Should().BeTrue();
        fs.File.ReadAllText("secrets.yaml").Should().Be(Yaml);
        fs.File.Exists("secrets.sec").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Unprotect_PlainFileExists_FailsWithoutOverwritingIt()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["secrets.yaml"] = new("edited: yes\n"),
            ["secrets.sec"] = new(Flip("pg: old\n"u8.ToArray())),
        });

        var result = SecretsProtection.Unprotect(fs, "secrets.yaml", Flip);

        result.IsFailure.Should().BeTrue();
        fs.File.ReadAllText("secrets.yaml").Should().Be("edited: yes\n");
    }

    [Theory]
    [InlineData(DataProtectionScope.LocalMachine)]
    [InlineData(DataProtectionScope.CurrentUser)]
    [Trait("Category", "Integration")]
    [Trait("Category", "Windows")]
    public void Dpapi_ProtectedFileOfEitherScope_ReadsBack(DataProtectionScope scope)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI exists only on Windows");
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { ["secrets.yaml"] = new(Yaml) });

        SecretsProtection.Protect(fs, "secrets.yaml", SecretsProtection.Protector(scope), SecretsProtection.Unprotect).IsSuccess.Should().BeTrue();

        Secrets.Load(fs, "secrets.yaml", SecretsProtection.Unprotect).Value["pg"]!.GetValue<string>().Should().Be("Host=db;Password=päss");
    }
}
