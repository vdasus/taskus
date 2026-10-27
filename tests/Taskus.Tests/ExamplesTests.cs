using System.IO.Abstractions;
using FluentAssertions;
using Serilog.Core;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;
using Taskus.Transports;

namespace Taskus.Tests;

/// <summary>Runs the map of each example over sample rows, so a broken example template fails the build.</summary>
public class ExamplesTests
{
    private static readonly FileSystem Fs = new();

    private static string Examples()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Taskus.slnx")))
            folder = folder.Parent;
        return Path.Combine(folder!.FullName, "examples");
    }

    private static RowMapper Mapper(string example)
    {
        var secrets = Secrets.Load(Fs, Path.Combine(Examples(), "secrets.yaml.example")).Value;
        return RowMapper.Create(TaskLoader.Load(Fs, Path.Combine(Examples(), example), secrets).Value.Map).Value;
    }

    private static async Task<List<Row>> MapClientsAsync(string example)
    {
        var mapper = Mapper(example);
        var source = new ExcelSource(Fs, Examples(), "clients.xlsx", null, null, null, Logger.None);
        var rows = await source.Units().Single().Rows.ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(r => mapper.Map(r.Values)).Select(r => r.IsSuccess ? r.Value : throw new InvalidOperationException(r.Error)).ToList();
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Priority", "High")]
    public async Task ExcelToPostgres_RenamesJoinsAndKeepsTypes()
    {
        var rows = await MapClientsAsync("excel-to-postgres.yaml");

        rows.Should().HaveCount(3);
        rows[0].Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["client_code"] = "K-001",
            ["full_name"] = "Jonas Jonaitis",
            ["email"] = "jonas.jonaitis@example.com",
            ["amount"] = 120.5m,
            ["invoice_date"] = new DateTime(2026, 10, 1),
            ["city"] = "Vilnius",
        });
        rows[1]["client_code"].Should().Be("K-002");
        rows[2]["amount"].Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExcelToApiPost_BuildsBodyFields()
    {
        var rows = await MapClientsAsync("excel-to-api-post.yaml");

        rows[0].Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["clientId"] = "K-001",
            ["name"] = "Jonas Jonaitis",
            ["email"] = "jonas.jonaitis@example.com",
            ["amount"] = 120.5m,
            ["dueDate"] = "2026-10-31",
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExcelToApiGet_MapsCodeAndCity()
    {
        var rows = await MapClientsAsync("excel-to-api-get.yaml");

        rows.Select(r => (r["code"], r["city"])).Should().Equal(("K-001", "Vilnius"), ("K-002", "Kaunas"), ("K-003", "Klaipėda"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExcelToMail_FillsMailFields()
    {
        var row = (await MapClientsAsync("excel-to-mail.yaml"))[0];

        row["to"].Should().Be("Jonas.Jonaitis@Example.com");
        row["cc"].Should().Be("accounting@example.com");
        row["subject"].Should().Be("Sąskaita K-001 (2026-10-01)");
        ((string)row["body"]!).Should().Contain("Sveiki, Jonas Jonaitis,").And.Contain("120.50 EUR").And.Contain("apmokėti iki 2026-10-31");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PostgresToExcel_ComputesVat()
    {
        var row = new Dictionary<string, object?> { ["id"] = 7L, ["client_name"] = "UAB Pavyzdys", ["amount"] = 100m, ["issued"] = new DateTime(2026, 10, 1) };

        var mapped = Mapper("postgres-to-excel.yaml").Map(row);

        mapped.IsSuccess.Should().BeTrue(mapped.IsFailure ? mapped.Error : "");
        mapped.Value.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["Sąskaitos Nr"] = "INV-7",
            ["Klientas"] = "UAB Pavyzdys",
            ["Suma be PVM"] = 100m,
            ["PVM 21%"] = 21m,
            ["Suma su PVM"] = 121m,
            ["Data"] = "2026-10-01",
        }, o => o.Using<object>(c => Convert.ToDecimal(c.Subject).Should().Be(Convert.ToDecimal(c.Expectation))).When(i => i.Path.StartsWith("[Suma") || i.Path.StartsWith("[PVM")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ApiToExcel_FlattensNestedObjects()
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = 1L,
            ["name"] = "Ona",
            ["email"] = "Ona@Example.com",
            ["address"] = new Dictionary<string, object?> { ["street"] = "Gedimino pr. 1", ["zipcode"] = "01103", ["city"] = "Vilnius" },
            ["company"] = null,
        };

        var mapped = Mapper("api-to-excel.yaml").Map(row);

        mapped.IsSuccess.Should().BeTrue(mapped.IsFailure ? mapped.Error : "");
        mapped.Value["Adresas"].Should().Be("Gedimino pr. 1, 01103 Vilnius");
        mapped.Value["El. paštas"].Should().Be("ona@example.com");
        mapped.Value["Įmonė"].Should().Be("-");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PostgresPhonesToExcel_RenamesColumns()
    {
        var mapped = Mapper("postgres-phones-to-excel.yaml").Map(new Dictionary<string, object?> { ["phone"] = "+37060000000", ["text"] = "Labas" });

        mapped.Value.Should().BeEquivalentTo(new Dictionary<string, object?> { ["Telefonas"] = "+37060000000", ["Tekstas"] = "Labas" });
    }
}
