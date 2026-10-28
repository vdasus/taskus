using System.Data.Common;
using Npgsql;

namespace Taskus.Transports;

/// <summary>PostgreSQL through Npgsql, which ships inside taskus.exe.</summary>
public sealed class PostgresDialect : SqlDialect
{
    public static readonly PostgresDialect Instance = new();

    public override DbConnection Connection(string connection) => new NpgsqlConnection(connection);

    public override string Describe(DbConnection db)
    {
        var pg = (NpgsqlConnection)db;
        return $"{pg.Host}:{pg.Port}/{pg.Database} as {pg.UserName}";
    }

    public override string Parameter(int number) => $"${number}";

    public override Result<object> Convert(object? value, Type type, string dataType) =>
        base.Convert(value, type, dataType).Map(v => v is DateTime dt ? FixKind(dt, dataType) : v);

    // Npgsql writes only UTC to timestamptz and refuses UTC for timestamp. Dates from Excel and text are local time.
    private static DateTime FixKind(DateTime value, string dataType) =>
        dataType.StartsWith("timestamp with time zone", StringComparison.Ordinal)
            ? value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
            : value.Kind == DateTimeKind.Utc ? DateTime.SpecifyKind(value.ToLocalTime(), DateTimeKind.Unspecified) : value;
}
