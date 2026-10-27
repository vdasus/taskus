using FluentAssertions;

namespace Taskus.Tests;

public class SecretMaskTests
{
    private static SecretMask Mask()
    {
        var mask = new SecretMask();
        mask.Add(new Dictionary<string, object?>
        {
            ["pg_main"] = "Host=db;Username=taskus;Password=pg-pass-1",
            ["api_token"] = "tok-123456",
            ["smtp_main"] = new Dictionary<object, object?>
            {
                ["server"] = "smtp.example.com",
                ["port"] = "587",
                ["user"] = "taskus",
                ["password"] = "mail-pass-2",
            },
        });
        return mask;
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    [InlineData("conn Host=db;Username=taskus;Password=pg-pass-1 failed", "conn *** failed")]
    [InlineData("auth with pg-pass-1", "auth with ***")]
    [InlineData("GET http://x/api?key=tok-123456", "GET http://x/api?key=***")]
    [InlineData("login mail-pass-2", "login ***")]
    public void Apply_MasksSecretValues(string text, string expected)
    {
        Mask().Apply(text).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Apply_KeepsNonSensitiveSettings()
    {
        var text = "smtp smtp.example.com:587 as taskus, read 587 rows";

        Mask().Apply(text).Should().Be(text);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Enrich_MasksNestedPropertyValues()
    {
        var sink = new CollectingSink();

        sink.Logger(Mask()).Information("row {@Values}", new Dictionary<string, object?> { ["url"] = "http://x?key=tok-123456", ["n"] = 1 });

        sink.Text.Should().NotContain("tok-123456").And.Contain("***");
    }
}
