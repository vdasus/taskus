using Serilog;
using Taskus.Config;
using Taskus.Mapping;

namespace Taskus.Pipeline;

/// <summary>Runs source → map → sink for every unit of the source and builds the run report.</summary>
public sealed class Runner(string task, ISource source, RowMapper mapper, ISink sink, ErrorPolicy onError, int maxErrors, ILogger log)
{
    private const int DryRunRowsLogged = 10;

    public async Task<RunReport> RunAsync(bool dryRun, CancellationToken ct)
    {
        var report = new RunReport(task) { DryRun = dryRun };
        var errors = 0;
        try
        {
            foreach (var unit in source.Units())
            {
                ct.ThrowIfCancellationRequested();
                var unitReport = new UnitReport(unit.Name);
                report.Units.Add(unitReport);
                log.Information("{Task}: processing {Unit}", task, unit.Name);

                var stop = await RunUnitAsync(unit, unitReport, dryRun, () => ++errors, ct);
                log.Information("{Task}: {Unit} read {Read}, written {Written}, errors {Errors}{Failure}",
                    task, unit.Name, unitReport.Read, unitReport.Written, unitReport.Errors.Count,
                    unitReport.Failure is null ? "" : $", failed: {unitReport.Failure}");
                if (stop)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Failure = ex.Message;
            log.Error(ex, "{Task}: run failed", task);
        }

        report.Finished = DateTime.Now;
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
            }

            var aborted = false;
            await foreach (var row in unit.Rows.WithCancellation(ct))
            {
                report.Read++;
                var written = await WriteAsync(session, row, report, ct);
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
            }
            else if (session is { AllOrNothing: true })
            {
                report.Written = 0;
                report.Failure = "rolled back";
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
            unit.Done(report.Completed);
        return stopRun;
    }

    private async Task<Result> WriteAsync(ISinkSession? session, SourceRow row, UnitReport report, CancellationToken ct)
    {
        var mapped = mapper.Map(row.Values);
        if (mapped.IsFailure)
            return mapped;
        if (session is not null)
            return await session.WriteAsync(mapped.Value, ct);

        if (report.Read <= DryRunRowsLogged)
            log.Information("{Task}: dry run, row {Row}: {@Values}", task, row.Number, mapped.Value);
        return Result.Success();
    }
}
