using MiniExcelLibs;
using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>Every file matching <c>pattern</c> in <c>folder</c> is one unit. The first sheet row holds the field names.</summary>
public sealed class ExcelSource(string folder, string pattern, string? sheet, string? processed, string? failed, ILogger log) : ISource
{
    public static Result<ISource> Create(Settings settings, ILogger log) =>
        settings.Required("folder").Map(folder => (ISource)new ExcelSource(
            folder,
            settings.Optional("pattern") ?? "*.xlsx",
            settings.Optional("sheet"),
            settings.Optional("processed"),
            settings.Optional("failed"),
            log));

    public IEnumerable<SourceUnit> Units() =>
        Directory.EnumerateFiles(folder, pattern)
            .Where(file => !Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) // Excel lock files
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => new SourceUnit(Path.GetFileName(file), Read(file).ToAsyncEnumerable(), done => Move(file, done)));

    private IEnumerable<SourceRow> Read(string file)
    {
        var number = 1; // row 1 is the header
        foreach (IDictionary<string, object?> row in MiniExcel.Query(file, useHeaderRow: true, sheetName: sheet))
        {
            number++;
            if (row.Values.All(v => v is null || v is string { Length: 0 }))
                continue;
            yield return new SourceRow(number, new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase));
        }
    }

    private void Move(string file, bool completed)
    {
        var target = completed ? processed : failed;
        if (target is null)
            return;

        Directory.CreateDirectory(target);
        var destination = Path.Combine(target, Path.GetFileName(file));
        if (File.Exists(destination))
            destination = Path.Combine(target, $"{Path.GetFileNameWithoutExtension(file)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(file)}");
        File.Move(file, destination);
        log.Information("Moved {File} to {Destination}", file, destination);
    }
}

/// <summary>Writes each unit to one .xlsx file. <c>file</c> is a template; <c>unit</c> is the source unit name.</summary>
public sealed class ExcelSink(string folder, TaskTemplate file, bool overwrite, string sheet) : ISink
{
    public static Result<ISink> Create(Settings settings)
    {
        var folder = settings.Required("folder");
        var file = settings.Required("file").Bind(text => TaskTemplate.Parse($"{settings.Path}.file", text));
        var overwrite = settings.Bool("overwrite", false);
        return Result.Combine("; ", folder, file, overwrite)
            .Map(() => (ISink)new ExcelSink(folder.Value, file.Value, overwrite.Value, settings.Optional("sheet") ?? "Sheet1"));
    }

    public Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct)
    {
        var variables = new Dictionary<string, object?> { ["unit"] = Path.GetFileNameWithoutExtension(unit) };
        var result = file.Evaluate("sink.file", variables)
            .Map(name => Path.Combine(folder, Convert.ToString(name)!))
            .Ensure(path => overwrite || !File.Exists(path), path => $"{path} already exists and overwrite is false")
            .Map(path => (ISinkSession)new Session(path, sheet));
        return Task.FromResult(result);
    }

    // shortcut: rows are buffered until the unit completes; fine for tens of thousands of rows, stream if files grow to millions.
    private sealed class Session(string path, string sheet) : ISinkSession
    {
        private readonly List<Dictionary<string, object?>> rows = [];

        public bool AllOrNothing => false; // onError decides: continue keeps good rows, stop writes no file

        public Task<Result> WriteAsync(Row row, CancellationToken ct)
        {
            rows.Add(new Dictionary<string, object?>(row));
            return Task.FromResult(Result.Success());
        }

        public Task<Result> CompleteAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            MiniExcel.SaveAs(path, rows, sheetName: sheet, overwriteFile: true);
            return Task.FromResult(Result.Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
