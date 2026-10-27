namespace Taskus.Pipeline;

/// <summary>A source row and its number: the sheet row for Excel, the ordinal for a query.</summary>
public sealed record SourceRow(int Number, Row Values);

/// <summary>
/// One independent piece of input: one Excel file, or one query result.
/// <paramref name="Done"/> runs after the unit with <c>true</c> when the sink completed it.
/// </summary>
public sealed record SourceUnit(string Name, IAsyncEnumerable<SourceRow> Rows, Action<bool> Done);

public interface ISource
{
    IEnumerable<SourceUnit> Units();
}

public interface ISink
{
    Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct);
}

/// <summary>Writes one unit. Disposing a session that was not completed discards or rolls back its writes.</summary>
public interface ISinkSession : IAsyncDisposable
{
    /// <summary>True when one failed row discards the whole unit, as in a single database transaction.</summary>
    bool AllOrNothing { get; }

    Task<Result> WriteAsync(Row row, CancellationToken ct);

    Task<Result> CompleteAsync(CancellationToken ct);
}
