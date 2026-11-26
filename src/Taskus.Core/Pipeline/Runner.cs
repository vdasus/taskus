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
            log.Error("{Task}: run failed: {Error}", task, ex.Message);
            log.Debug("{Task}: {Details}", task, ex.ToString()); // as a property, so secrets in the text are masked
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

            // Ctrl+C: rows a sink keeps cannot be taken back, so such a unit finishes and its input is moved;
            // otherwise the next run would write them again. A second Ctrl+C ends the process at once.
            var rowCt = session is { KeepsNothingOnFailure: false } ? CancellationToken.None : ct;
            using var notice = rowCt == ct ? default : ct.Register(() => log.Warning(
                "{Task}: cancel requested, finishing {Unit} first so its written rows are not written again; press Ctrl+C again to end now",
                task, unit.Name));

            var aborted = false;
            await foreach (var row in unit.Rows.WithCancellation(rowCt))
            {
                report.Read++;
                log.Verbose("{Task}: {Unit} row {Row} read: {@Values}", task, unit.Name, row.Number, Hide(row.Values));
                var written = await WriteAsync(session, unit.Name, row, report, rowCt);
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
                var completed = session is null ? Result.Success() : await session.CompleteAsync(rowCt);
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
            log.Error("{Task}: {Unit} failed: {Error}", task, unit.Name, ex.Message);
            log.Debug("{Task}: {Unit} {Details}", task, unit.Name, ex.ToString());
            stopRun = onError == ErrorPolicy.Stop;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }

        // Before the input is moved: a failed move leaves the written rows where they are.
        if (!report.Completed && session is { KeepsNothingOnFailure: true })
            report.Written = 0;

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
                log.Error("{Task}: {Unit} input not moved; the next run reads it again: {Error}", task, unit.Name, ex.Message);
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
