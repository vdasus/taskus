using System.Diagnostics;
using Serilog;
using Taskus.Config;
using Taskus.Mapping;

namespace Taskus.Pipeline;

/// <summary>Runs source → map → sink for every unit of the source and builds the run report.</summary>
/// <param name="logExclude">Field names whose values are replaced with <c>***</c> wherever rows are logged.</param>
public sealed class Runner(
    string task, ISource source, RowMapper mapper, ISink sink, ErrorPolicy onError, int maxErrors, ILogger log,
    IReadOnlyCollection<string>? logExclude = null)
{
    private const int DryRunRowsLogged = 10;
    private readonly HashSet<string> hidden = new(logExclude ?? [], StringComparer.OrdinalIgnoreCase);

    public async Task<RunReport> RunAsync(bool dryRun, CancellationToken ct)
    {
        var report = new RunReport(task);
        var errors = 0;
        var clock = Stopwatch.StartNew();
        log.Debug("{Task}: run started, dry run {DryRun}, onError {OnError}, maxErrors {MaxErrors}", task, dryRun, onError, maxErrors);
        try
        {
            foreach (var unit in source.Units())
            {
                ct.ThrowIfCancellationRequested();
                var unitReport = new UnitReport(unit.Name);
                report.Units.Add(unitReport);
                log.Information("{Task}: processing {Unit}", task, unit.Name);

                var unitClock = Stopwatch.StartNew();
                var stop = await RunUnitAsync(unit, unitReport, dryRun, () => ++errors, ct);
                log.Information("{Task}: {Unit} read {Read}, written {Written}, errors {Errors}, completed {Completed} in {ElapsedMs} ms",
                    task, unit.Name, unitReport.Read, unitReport.Written, unitReport.Errors.Count, unitReport.Completed, unitClock.ElapsedMilliseconds);
                if (unitReport.Failure is not null)
                    log.Warning("{Task}: {Unit} failed: {Failure}", task, unit.Name, unitReport.Failure);
                if (stop)
                {
                    log.Warning("{Task}: run stopped after {Errors} errors (onError {OnError}, maxErrors {MaxErrors})", task, errors, onError, maxErrors);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Failure = ex.Message;
            log.Error(ex, "{Task}: run failed", task);
        }

        report.Finished = DateTime.Now;
        log.Debug("{Task}: run finished in {ElapsedMs} ms, {Units} units, {Errors} errors", task, clock.ElapsedMilliseconds, report.Units.Count, errors);
        return report;
    }

    /// <returns>True when the whole run must stop.</returns>
    private async Task<bool> RunUnitAsync(SourceUnit unit, UnitReport report, bool dryRun, Func<int> countError, CancellationToken ct)
    {
        ISinkSession? session = null;
        var stopRun = false;
        try
        {
            if (!dryRun)
            {
                var opened = await sink.OpenAsync(unit.Name, ct);
                if (opened.IsFailure)
                {
                    // Nothing was read: the input stays where it is, so the next run retries it (e.g. database down).
                    report.Failure = opened.Error;
                    return onError == ErrorPolicy.Stop;
                }

                session = opened.Value;
                log.Debug("{Task}: {Unit} sink opened, all or nothing {AllOrNothing}", task, unit.Name, session.AllOrNothing);
            }

            var aborted = false;
            await foreach (var row in unit.Rows.WithCancellation(ct))
            {
                report.Read++;
                log.Verbose("{Task}: {Unit} row {Row} read: {@Values}", task, unit.Name, row.Number, Hide(row.Values));
                var written = await WriteAsync(session, unit.Name, row, report, ct);
                if (written.IsSuccess)
                {
                    report.Written++;
                    continue;
                }

                report.Errors.Add(new RowError(row.Number, written.Error));
                log.Warning("{Task}: {Unit} row {Row}: {Error}", task, unit.Name, row.Number, written.Error);
                var total = countError();
                stopRun = onError == ErrorPolicy.Stop || (maxErrors > 0 && total >= maxErrors);
                if (stopRun || session is { AllOrNothing: true })
                {
                    aborted = true;
                    break;
                }
            }

            if (!aborted)
            {
                var completed = session is null ? Result.Success() : await session.CompleteAsync(ct);
                report.Completed = completed.IsSuccess;
                report.Failure = completed.IsFailure ? completed.Error : null;
                log.Debug("{Task}: {Unit} sink completed {Completed}", task, unit.Name, report.Completed);
            }
            else if (session is { AllOrNothing: true })
            {
                report.Written = 0;
                report.Failure = "rolled back";
                log.Debug("{Task}: {Unit} rolled back", task, unit.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Failure = ex.Message;
            log.Error(ex, "{Task}: {Unit} failed", task, unit.Name);
            stopRun = onError == ErrorPolicy.Stop;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }

        if (!dryRun)
        {
            try
            {
                unit.Done(report.Completed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The rows are written but the input stays: the next run reads it again. Say so loudly.
                report.Completed = false;
                report.Failure = $"{(report.Failure is null ? "" : report.Failure + "; ")}input not moved, the next run reads it again: {ex.Message}";
                log.Error(ex, "{Task}: {Unit} input not moved; the next run reads it again", task, unit.Name);
            }
        }

        return stopRun;
    }

    private async Task<Result> WriteAsync(ISinkSession? session, string unit, SourceRow row, UnitReport report, CancellationToken ct)
    {
        var mapped = mapper.Map(row.Values);
        if (mapped.IsFailure)
            return mapped;
        log.Verbose("{Task}: {Unit} row {Row} mapped: {@Values}", task, unit, row.Number, Hide(mapped.Value));
        if (session is not null)
            return await session.WriteAsync(mapped.Value, ct);

        if (report.Read <= DryRunRowsLogged)
            log.Information("{Task}: dry run, row {Row}: {@Values}", task, row.Number, Hide(mapped.Value));
        return Result.Success();
    }

    private Row Hide(Row row) =>
        hidden.Count == 0 || !row.Keys.Any(hidden.Contains)
            ? row
            : row.ToDictionary(kv => kv.Key, kv => hidden.Contains(kv.Key) ? SecretMask.Mask : kv.Value, StringComparer.OrdinalIgnoreCase);
}
