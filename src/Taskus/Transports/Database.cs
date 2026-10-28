using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Serilog;
using Taskus.Config;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>What differs between the ADO.NET providers behind <see cref="DbSource"/> and <see cref="DbSink"/>.</summary>
public abstract class SqlDialect
{
    public abstract DbConnection Connection(string connection);

    /// <summary>Server, database and user of an open connection, for the log. Never the password.</summary>
    public abstract string Describe(DbConnection db);

    /// <summary>Placeholder of insert parameter <paramref name="number"/>, counted from 1.</summary>
    public abstract string Parameter(int number);

    /// <summary>Converts a mapped value to the type of its column.</summary>
    public virtual Result<object> Convert(object? value, Type type, string dataType) => DbValues.Convert(value, type, dataType);
}

/// <summary>The result of <c>query</c> is one unit.</summary>
public sealed class DbSource(SqlDialect dialect, string connection, string query, ILogger log) : ISource
{
    public IEnumerable<SourceUnit> Units()
    {
        yield return new SourceUnit("query", ReadAsync(), _ => { });
    }

    private async IAsyncEnumerable<SourceRow> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = dialect.Connection(connection);
        await db.OpenAsync(ct);
        log.Debug("Connected to {Server}", dialect.Describe(db));
        log.Debug("Query: {Query}", query);
        await using var command = db.CreateCommand();
        command.CommandText = query;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var number = 0;
        var finished = false;
        try
        {
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                yield return new SourceRow(++number, row);
            }

            finished = true;
        }
        finally
        {
            // A run that stopped early: closing the reader would otherwise read every remaining row.
            if (!finished)
                command.Cancel();
        }

        log.Debug("Query returned {RowCount} rows", number);
    }
}

/// <summary>Inserts rows into <c>table</c>. Columns are the mapped field names; values convert to the column types.</summary>
public sealed class DbSink(SqlDialect dialect, string connection, string table, TransactionScope transaction, ILogger log) : ISink
{
    public async Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct)
    {
        var db = dialect.Connection(connection);
        try
        {
            await db.OpenAsync(ct);
            var tx = transaction == TransactionScope.All ? await db.BeginTransactionAsync(ct) : null;
            log.Debug("{Unit}: connected to {Server}, transaction {Transaction}", unit, dialect.Describe(db), transaction);
            return new Session(dialect, db, tx, table, log);
        }
        catch (Exception ex)
        {
            await db.DisposeAsync();
            if (ex is DbException)
                return Result.Failure<ISinkSession>(ex.Message);
            throw;
        }
    }

    private sealed class Session(SqlDialect dialect, DbConnection db, DbTransaction? tx, string table, ILogger log) : ISinkSession
    {
        private DbCommand? insert;
        private bool committed;
        private IReadOnlyList<(string Name, Type Type, string DataType)> columns = [];

        public bool AllOrNothing => tx is not null;

        public async Task<Result> WriteAsync(Row row, CancellationToken ct)
        {
            try
            {
                if (insert is null && row.Keys.FirstOrDefault(UnsafeName) is { } unsafeName)
                    return Result.Failure($"field name '{unsafeName}' must not contain \", \\ or control characters");
                insert ??= await InsertAsync(row.Keys.ToList(), ct);
                if (row.Count != columns.Count || columns.Any(c => !row.ContainsKey(c.Name)))
                    return Result.Failure($"fields differ from the first row: {string.Join(", ", row.Keys)}");

                for (var i = 0; i < columns.Count; i++)
                {
                    var (name, type, dataType) = columns[i];
                    var value = dialect.Convert(row[name], type, dataType);
                    if (value.IsFailure)
                        return Result.Failure($"{name}: {value.Error}");
                    insert.Parameters[i].Value = value.Value;
                }

                await insert.ExecuteNonQueryAsync(ct);
                return Result.Success();
            }
            catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException)
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
            try
            {
                if (insert is not null)
                    await insert.DisposeAsync();
                if (tx is not null)
                {
                    await tx.DisposeAsync(); // rolls back when not committed
                    if (!committed)
                        log.Debug("Rolled back inserts into {Table}", table);
                }
            }
            finally
            {
                await db.DisposeAsync(); // also when a broken connection makes the rollback throw
            }
        }

        private async Task<DbCommand> InsertAsync(List<string> fields, CancellationToken ct)
        {
            var target = string.Join('.', table.Split('.').Select(Quote));
            var list = string.Join(", ", fields.Select(Quote));

            await using (var probe = Command($"select {list} from {target} where 1 = 0"))
            await using (var reader = await probe.ExecuteReaderAsync(ct))
                columns = fields.Select((f, i) => (f, reader.GetFieldType(i), reader.GetDataTypeName(i))).ToList();

            var values = string.Join(", ", fields.Select((_, i) => dialect.Parameter(i + 1)));
            var command = Command($"insert into {target} ({list}) values ({values})");
            // Not prepared: Npgsql and ODBC need parameter types up front for that; unprepared, they take them from each value.
            foreach (var _ in fields)
                command.Parameters.Add(command.CreateParameter());
            log.Debug("Insert {Sql}; column types {ColumnTypes}", command.CommandText, columns.Select(c => $"{c.Name}: {c.DataType}"));
            return command;
        }

        private DbCommand Command(string sql)
        {
            var command = db.CreateCommand();
            command.CommandText = sql;
            command.Transaction = tx;
            return command;
        }

        // Field names can come from Excel headers. Without ANSI_QUOTES, MySQL reads "..." as a string with \ escapes.
        private static bool UnsafeName(string name) => name.Any(c => c is '"' or '\\' || char.IsControl(c));

        // ANSI quotes: PostgreSQL, SQL Server, Oracle, SQL Anywhere. MySQL needs sql_mode ANSI_QUOTES.
        private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}

/// <summary>Converts mapped values (text, Excel numbers and dates) to the CLR type of a column.</summary>
internal static class DbValues
{
    public static Result<object> Convert(object? value, Type type, string dataType)
    {
        if (value is null || (value is string { Length: 0 } && type != typeof(string)))
            return DBNull.Value;

        try
        {
            return value switch
            {
                _ when type.IsInstanceOfType(value) => value,
                _ when type == typeof(string) => System.Convert.ToString(value, CultureInfo.InvariantCulture)!,
                string s when type == typeof(DateTime) => DateTime.Parse(s, CultureInfo.InvariantCulture),
                double d when type == typeof(DateTime) => DateTime.FromOADate(d),
                string s when type == typeof(Guid) => Guid.Parse(s),
                string s when type == typeof(bool) => ParseBool(s),
                _ => System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return Result.Failure<object>($"'{value}' cannot be converted to {dataType}");
        }
    }

    private static bool ParseBool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "1" or "yes" or "y" or "true" or "t" => true,
        "0" or "no" or "n" or "false" or "f" => false,
        _ => throw new FormatException(),
    };
}
