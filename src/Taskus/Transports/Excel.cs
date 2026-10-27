using MiniExcelLibs;
using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>Every file matching <c>pattern</c> in <c>folder</c> is one unit. The first sheet row holds the field names.</summary>
public sealed class ExcelSource(string folder, string pattern, string? sheet, string? processed, string? failed, ILogger log) : ISource
{
    public IEnumerable<SourceUnit> Units()
    {
        var files = Directory.EnumerateFiles(folder, pattern)
            .Where(file => !Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) // Excel lock files
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        log.Debug("{FileCount} files match {Pattern} in {Folder}", files.Count, pattern, folder);
        return files.Select(file => new SourceUnit(Path.GetFileName(file), Read(file).ToAsyncEnumerable(), done => Move(file, done)));
    }

    private IEnumerable<SourceRow> Read(string file)
    {
        log.Debug("Reading {File}, sheet {Sheet}", file, sheet ?? "(first)");
        var number = 1; // row 1 is the header
        var empty = 0;
        foreach (IDictionary<string, object?> row in MiniExcel.Query(file, useHeaderRow: true, sheetName: sheet))
        {
            number++;
            if (row.Values.All(v => v is null || v is string { Length: 0 }))
            {
                empty++;
                continue;
            }

            yield return new SourceRow(number, new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase));
        }

        log.Debug("{File}: last sheet row {LastRow}, {EmptyRows} empty rows skipped", file, number, empty);
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
public sealed class ExcelSink(string folder, TaskTemplate file, bool overwrite, string sheet, ILogger log) : ISink
{
    public Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct)
    {
        var variables = new Dictionary<string, object?> { ["unit"] = Path.GetFileNameWithoutExtension(unit) };
        var result = file.Evaluate("sink.file", variables)
            .Map(name => Path.Combine(folder, Convert.ToString(name)!))
            .Ensure(path => overwrite || !File.Exists(path), path => $"{path} already exists and overwrite is false")
            .Tap(path => log.Debug("{Unit}: output file {Path}, overwrite {Overwrite}", unit, path, overwrite))
            .Map(path => (ISinkSession)new Session(path, sheet, log));
        return Task.FromResult(result);
    }

    // shortcut: rows are buffered until the unit completes; fine for tens of thousands of rows, stream if files grow to millions.
    private sealed class Session(string path, string sheet, ILogger log) : ISinkSession
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
            log.Debug("Wrote {RowCount} rows to {Path}, sheet {Sheet}", rows.Count, path, sheet);
            return Task.FromResult(Result.Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
