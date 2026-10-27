using System.Globalization;
using System.Runtime.CompilerServices;
using Npgsql;
using Serilog;
using Taskus.Config;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>The result of <c>query</c> is one unit.</summary>
public sealed class PostgresSource(string connection, string query, ILogger log) : ISource
{
    public IEnumerable<SourceUnit> Units()
    {
        yield return new SourceUnit("query", ReadAsync(), _ => { });
    }

    private async IAsyncEnumerable<SourceRow> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        log.Debug("Connected to {Host}:{Port}/{Database} as {User}", db.Host, db.Port, db.Database, db.UserName);
        log.Debug("Query: {Query}", query);
        await using var command = new NpgsqlCommand(query, db);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var number = 0;
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return new SourceRow(++number, row);
        }

        log.Debug("Query returned {RowCount} rows", number);
    }
}

/// <summary>Inserts rows into <c>table</c>. Columns are the mapped field names; values convert to the column types.</summary>
public sealed class PostgresSink(string connection, string table, TransactionScope transaction, ILogger log) : ISink
{
    public async Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct)
    {
        var db = new NpgsqlConnection(connection);
        try
        {
            await db.OpenAsync(ct);
            var tx = transaction == TransactionScope.All ? await db.BeginTransactionAsync(ct) : null;
            log.Debug("{Unit}: connected to {Host}:{Port}/{Database} as {User}, transaction {Transaction}",
                unit, db.Host, db.Port, db.Database, db.UserName, transaction);
            return new Session(db, tx, table, log);
        }
        catch (Exception ex)
        {
            await db.DisposeAsync();
            if (ex is NpgsqlException)
                return Result.Failure<ISinkSession>(ex.Message);
            throw;
        }
    }

    private sealed class Session(NpgsqlConnection db, NpgsqlTransaction? tx, string table, ILogger log) : ISinkSession
    {
        private NpgsqlCommand? insert;
        private bool committed;
        private IReadOnlyList<(string Name, Type Type, string DataType)> columns = [];

        public bool AllOrNothing => tx is not null;

        public async Task<Result> WriteAsync(Row row, CancellationToken ct)
        {
            try
            {
                insert ??= await PrepareAsync(row.Keys.ToList(), ct);
                if (row.Count != columns.Count || columns.Any(c => !row.ContainsKey(c.Name)))
                    return Result.Failure($"fields differ from the first row: {string.Join(", ", row.Keys)}");

                for (var i = 0; i < columns.Count; i++)
                {
                    var (name, type, dataType) = columns[i];
                    var value = PgValues.Convert(row[name], type, dataType);
                    if (value.IsFailure)
                        return Result.Failure($"{name}: {value.Error}");
                    insert.Parameters[i].Value = value.Value;
                }

                await insert.ExecuteNonQueryAsync(ct);
                return Result.Success();
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidCastException or FormatException)
            {
                return Result.Failure(ex.Message);
            }
        }

        public async Task<Result> CompleteAsync(CancellationToken ct)
        {
            if (tx is not null)
            {
                await tx.CommitAsync(ct);
                committed = true;
                log.Debug("Committed inserts into {Table}", table);
            }

            return Result.Success();
        }

        public async ValueTask DisposeAsync()
        {
            if (insert is not null)
                await insert.DisposeAsync();
            if (tx is not null)
            {
                await tx.DisposeAsync(); // rolls back when not committed
                if (!committed)
                    log.Debug("Rolled back inserts into {Table}", table);
            }
            await db.DisposeAsync();
        }

        private async Task<NpgsqlCommand> PrepareAsync(List<string> fields, CancellationToken ct)
        {
            var target = string.Join('.', table.Split('.').Select(Quote));
            var list = string.Join(", ", fields.Select(Quote));

            await using (var probe = new NpgsqlCommand($"select {list} from {target} where false", db, tx))
            await using (var reader = await probe.ExecuteReaderAsync(ct))
                columns = fields.Select((f, i) => (f, reader.GetFieldType(i), reader.GetDataTypeName(i))).ToList();

            var values = string.Join(", ", fields.Select((_, i) => $"${i + 1}"));
            var command = new NpgsqlCommand($"insert into {target} ({list}) values ({values})", db, tx);
            foreach (var _ in fields)
                command.Parameters.Add(new NpgsqlParameter());
            await command.PrepareAsync(ct);
            log.Debug("Prepared {Sql}; column types {ColumnTypes}", command.CommandText, columns.Select(c => $"{c.Name}: {c.DataType}"));
            return command;
        }

        private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}

/// <summary>Converts mapped values (text, Excel numbers and dates) to the CLR type of a column.</summary>
internal static class PgValues
{
    public static Result<object> Convert(object? value, Type type, string dataType)
    {
        if (value is null || (value is string { Length: 0 } && type != typeof(string)))
            return DBNull.Value;

        try
        {
            var converted = value switch
            {
                _ when type.IsInstanceOfType(value) => value,
                _ when type == typeof(string) => System.Convert.ToString(value, CultureInfo.InvariantCulture)!,
                string s when type == typeof(DateTime) => DateTime.Parse(s, CultureInfo.InvariantCulture),
                double d when type == typeof(DateTime) => DateTime.FromOADate(d),
                string s when type == typeof(Guid) => Guid.Parse(s),
                string s when type == typeof(bool) => ParseBool(s),
                _ => System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture),
            };
            return converted is DateTime dt ? FixKind(dt, dataType) : converted;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return Result.Failure<object>($"'{value}' cannot be converted to {dataType}");
        }
    }

    // Npgsql writes only UTC to timestamptz and refuses UTC for timestamp. Dates from Excel and text are local time.
    private static DateTime FixKind(DateTime value, string dataType) =>
        dataType.StartsWith("timestamp with time zone", StringComparison.Ordinal)
            ? value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
            : value.Kind == DateTimeKind.Utc ? DateTime.SpecifyKind(value.ToLocalTime(), DateTimeKind.Unspecified) : value;

    private static bool ParseBool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "1" or "yes" or "y" or "true" or "t" => true,
        "0" or "no" or "n" or "false" or "f" => false,
        _ => throw new FormatException(),
    };
}
