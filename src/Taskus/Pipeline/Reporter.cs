using System.Text;
using MimeKit;
using MiniExcelLibs;
using Serilog;
using Taskus.Config;
using Taskus.Transports;

namespace Taskus.Pipeline;

/// <summary>Saves the run report as .xlsx and mails it, as <c>report</c> in the task file says.</summary>
public static class Reporter
{
    private const int ErrorsInMail = 50;

    public static bool ShouldReport(ReportWhen when, RunReport report) =>
        when == ReportWhen.Always || (when == ReportWhen.OnError && report.HasErrors);

    public static async Task<Result> SendAsync(TaskDefinition task, RunReport report, ILogger log, CancellationToken ct)
    {
        if (!ShouldReport(task.Report.When, report))
        {
            log.Debug("{Task}: no report (when {When}, errors {HasErrors})", task.Name, task.Report.When, report.HasErrors);
            return Result.Success();
        }

        try
        {
            string? file = null;
            if (task.Report.Folder is { } folder)
            {
                file = Save(folder, report);
                log.Information("{Task}: report saved to {File}", task.Name, file);
            }

            if (task.Report.MailTo is { } to && task.Smtp is { } smtp)
            {
                var options = SmtpOptions.Create(smtp);
                if (options.IsFailure)
                    return options;
                await MailAsync(options.Value, to, report, file, log, ct);
                log.Information("{Task}: report mailed to {To}", task.Name, to);
            }

            return Result.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error(ex, "{Task}: report failed", task.Name);
            return Result.Failure($"report: {ex.Message}");
        }
    }

    public static string Save(string folder, RunReport report)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{report.Task}_{report.Started:yyyyMMdd_HHmmss}.xlsx");
        var sheets = new Dictionary<string, object>
        {
            ["Summary"] = report.Units
                .Select(u => new SummaryLine(u.Name, u.Completed ? "completed" : "failed", u.Read, u.Written, u.Errors.Count, u.Failure ?? ""))
                .Prepend(new SummaryLine("(run)", report.Failure is null ? "" : "failed", 0, 0, 0, report.Failure ?? ""))
                .ToList(),
            ["Errors"] = report.Units
                .SelectMany(u => u.Errors.Select(e => new ErrorLine(u.Name, e.Row, e.Message)))
                .ToList(),
        };
        MiniExcel.SaveAs(file, sheets, overwriteFile: true);
        return file;
    }

    public static string Text(RunReport report)
    {
        var text = new StringBuilder()
            .AppendLine($"Task: {report.Task}")
            .AppendLine($"Started: {report.Started:yyyy-MM-dd HH:mm:ss}, finished: {report.Finished:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Result: {(report.HasErrors ? "errors" : "success")}");
        if (report.Failure is not null)
            text.AppendLine($"Run failed: {report.Failure}");
        if (report.Units.Count == 0)
            text.AppendLine("No input.");

        foreach (var unit in report.Units)
        {
            text.AppendLine()
                .AppendLine($"{unit.Name}: {(unit.Completed ? "completed" : "failed")}, read {unit.Read}, written {unit.Written}, errors {unit.Errors.Count}");
            if (unit.Failure is not null)
                text.AppendLine($"  {unit.Failure}");
            foreach (var error in unit.Errors.Take(ErrorsInMail))
                text.AppendLine($"  row {error.Row}: {error.Message}");
            if (unit.Errors.Count > ErrorsInMail)
                text.AppendLine($"  ... {unit.Errors.Count - ErrorsInMail} more, see the attached report");
        }

        return text.ToString();
    }

    private static async Task MailAsync(SmtpOptions options, string to, RunReport report, string? attachment, ILogger log, CancellationToken ct)
    {
        var message = options.Message(to, $"Taskus: {report.Task}: {(report.HasErrors ? "errors" : "success")}");
        var body = new BodyBuilder { TextBody = Text(report) };
        if (attachment is not null)
            await body.Attachments.AddAsync(attachment, ct);
        message.Body = body.ToMessageBody();

        using var client = await options.ConnectAsync(log, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    private sealed record SummaryLine(string Unit, string Status, int Read, int Written, int Errors, string Failure);

    private sealed record ErrorLine(string Unit, int Row, string Message);
}
