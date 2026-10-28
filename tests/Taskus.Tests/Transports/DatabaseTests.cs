using System.Data.Odbc;
using FluentAssertions;
using Npgsql;
using Serilog.Core;
using Taskus.Config;
using Taskus.Transports;
using Testcontainers.PostgreSql;

namespace Taskus.Tests.Transports;

/// <summary>
/// PostgreSQL in Docker, started once for <see cref="DatabaseTests"/>. Without Docker the tests are skipped, so
/// building and testing Taskus needs no Docker. TASKUS_DB_TESTS=required turns the skip into a failure (CI).
/// </summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    public static readonly bool Required = Environment.GetEnvironmentVariable("TASKUS_DB_TESTS") == "required";

    private PostgreSqlContainer? container;

    /// <summary>Npgsql connection string; null when Docker is not available.</summary>
    public string? Connection { get; private set; }

    public string SkipReason { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        try
        {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
            Connection = container.GetConnectionString();
        }
        catch (Exception ex) when (!Required)
        {
            SkipReason = $"Docker is not available: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }
}

public class DatabaseTests(PostgresContainer db) : IClassFixture<PostgresContainer>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Category", "Integration")]
    [Trait("Category", "Docker")]
    [Trait("Priority", "High")]
    [InlineData("postgres")]
    [InlineData("odbc")]
    public async Task Sink_ConvertsAndWritesRows_SourceReadsThemBack(string kind)
    {
        var (dialect, connection) = await ConnectAsync(kind);
        var table = await CreateTableAsync();
        var sink = new DbSink(dialect, connection, table, TransactionScope.PerRow, Logger.None);

        await using (var session = (await sink.OpenAsync("unit", Ct)).Value)
        {
            (await session.WriteAsync(Row("K-1", "12.5", new DateTime(2026, 10, 27).ToOADate(), "yes"), Ct)).IsSuccess.Should().BeTrue();
            (await session.WriteAsync(Row("K-2", "", null, null), Ct)).IsSuccess.Should().BeTrue();
            (await session.CompleteAsync(Ct)).IsSuccess.Should().BeTrue();
        }

        var rows = await ReadAsync(dialect, connection, table);

        rows.Should().HaveCount(2);
        rows[0]["code"].Should().Be("K-1");
        Convert.ToDecimal(rows[0]["amount"]).Should().Be(12.5m);
        rows[0]["issued"].Should().Be(new DateTime(2026, 10, 27));
        rows[0]["active"].Should().Be(true);
        rows[1]["amount"].Should().BeNull();
        rows[1]["issued"].Should().BeNull();
    }

    [Theory]
    [Trait("Category", "Integration")]
    [Trait("Category", "Docker")]
    [InlineData("postgres")]
    [InlineData("odbc")]
    public async Task Sink_AllTransactionNotCompleted_RollsBack(string kind)
    {
        var (dialect, connection) = await ConnectAsync(kind);
        var table = await CreateTableAsync();
        var sink = new DbSink(dialect, connection, table, TransactionScope.All, Logger.None);

        await using (var session = (await sink.OpenAsync("unit", Ct)).Value)
        {
            session.AllOrNothing.Should().BeTrue();
            (await session.WriteAsync(Row("K-1", "1", null, null), Ct)).IsSuccess.Should().BeTrue();
        }

        (await ReadAsync(dialect, connection, table)).Should().BeEmpty();
    }

    [Theory]
    [Trait("Category", "Integration")]
    [Trait("Category", "Docker")]
    [InlineData("postgres")]
    [InlineData("odbc")]
    public async Task Sink_BadValueOrMissingTable_FailsTheRow(string kind)
    {
        var (dialect, connection) = await ConnectAsync(kind);
        var table = await CreateTableAsync();

        await using (var session = (await new DbSink(dialect, connection, table, TransactionScope.PerRow, Logger.None).OpenAsync("unit", Ct)).Value)
            (await session.WriteAsync(Row("K-1", "abc", null, null), Ct)).Error.Should().StartWith("amount: 'abc' cannot be converted to numeric");

        await using (var session = (await new DbSink(dialect, connection, "no_such_table", TransactionScope.PerRow, Logger.None).OpenAsync("unit", Ct)).Value)
            (await session.WriteAsync(Row("K-1", "1", null, null), Ct)).Error.Should().Contain("no_such_table");
    }

    private async Task<(SqlDialect Dialect, string Connection)> ConnectAsync(string kind)
    {
        Assert.SkipWhen(db.Connection is null, db.SkipReason);
        if (kind == "postgres")
            return (PostgresDialect.Instance, db.Connection!);

        var pg = new NpgsqlConnectionStringBuilder(db.Connection);
        // BoolsAsChar=0: psqlODBC reports boolean columns as text by default.
        var driver = OperatingSystem.IsWindows() ? "PostgreSQL Unicode(x64)" : "PostgreSQL Unicode";
        var connection = $"Driver={{{driver}}};Server={pg.Host};Port={pg.Port};Database={pg.Database};Uid={pg.Username};Pwd={pg.Password};BoolsAsChar=0";
        try
        {
            await using var probe = new OdbcConnection(connection);
            await probe.OpenAsync(Ct);
        }
        catch (Exception ex) when (!PostgresContainer.Required && ex is OdbcException or DllNotFoundException or TypeInitializationException)
        {
            Assert.Skip($"The psqlODBC driver ({driver}) is not installed: {ex.Message}");
        }

        return (OdbcDialect.Instance, connection);
    }

    private async Task<string> CreateTableAsync()
    {
        var table = $"t_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(db.Connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"create table {table} (code text, amount numeric, issued timestamp, active boolean)", connection);
        await command.ExecuteNonQueryAsync(Ct);
        return table;
    }

    private static async Task<List<Row>> ReadAsync(SqlDialect dialect, string connection, string table)
    {
        var source = new DbSource(dialect, connection, $"select * from {table} order by code", Logger.None);
        return await source.Units().Single().Rows.Select(r => r.Values).ToListAsync(Ct);
    }

    private static Row Row(string code, object? amount, object? issued, object? active) =>
        new Dictionary<string, object?> { ["code"] = code, ["amount"] = amount, ["issued"] = issued, ["active"] = active };
}
