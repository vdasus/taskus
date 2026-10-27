using System.IO.Abstractions;
using MiniExcelLibs;
using Serilog;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>Every file matching <c>pattern</c> in <c>folder</c> is one unit. The first sheet row holds the field names.</summary>
public sealed class ExcelSource(IFileSystem fs, string folder, string pattern, string? sheet, string? processed, string? failed, ILogger log) : ISource
{
    public IEnumerable<SourceUnit> Units()
    {
        var files = fs.Directory.EnumerateFiles(folder, pattern)
            .Where(file => !fs.Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) // Excel lock files
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        log.Debug("{FileCount} files match {Pattern} in {Folder}", files.Count, pattern, folder);
        return files.Select(file => new SourceUnit(fs.Path.GetFileName(file), Read(file).ToAsyncEnumerable(), done => Move(file, done)));
    }

    private IEnumerable<SourceRow> Read(string file)
    {
        log.Debug("Reading {File}, sheet {Sheet}", file, sheet ?? "(first)");
        var number = 1; // row 1 is the header
        var empty = 0;
        using var stream = fs.File.OpenRead(file);
        foreach (IDictionary<string, object?> row in stream.Query(useHeaderRow: true, sheetName: sheet, excelType: ExcelType.XLSX))
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

        fs.Directory.CreateDirectory(target);
        var destination = fs.Path.Combine(target, fs.Path.GetFileName(file));
        if (fs.File.Exists(destination))
            destination = fs.Path.Combine(target, $"{fs.Path.GetFileNameWithoutExtension(file)}_{DateTime.Now:yyyyMMdd_HHmmss}{fs.Path.GetExtension(file)}");
        fs.File.Move(file, destination);
        log.Information("Moved {File} to {Destination}", file, destination);
    }
}

/// <summary>Writes each unit to one .xlsx file. <c>file</c> is a template; <c>unit</c> is the source unit name.</summary>
public sealed class ExcelSink(IFileSystem fs, string folder, TaskTemplate file, bool overwrite, string sheet, ILogger log) : ISink
{
    // Two units with one output file would overwrite each other's rows while both count as completed.
    private readonly HashSet<string> opened = new(StringComparer.OrdinalIgnoreCase);

    public Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct)
    {
        var variables = new Dictionary<string, object?> { ["unit"] = fs.Path.GetFileNameWithoutExtension(unit) };
        var result = file.Evaluate("sink.file", variables)
            .Map(name => fs.Path.GetFullPath(fs.Path.Combine(folder, Convert.ToString(name)!)))
            .Ensure(path => opened.Add(path), path => $"{path} is already the output of another unit in this run; put {{{{ unit }}}} in sink.file")
            .Ensure(path => overwrite || !fs.File.Exists(path), path => $"{path} already exists and overwrite is false")
            .Tap(path => log.Debug("{Unit}: output file {Path}, overwrite {Overwrite}", unit, path, overwrite))
            .Map(path => (ISinkSession)new Session(fs, path, sheet, log));
        return Task.FromResult(result);
    }

    // shortcut: rows are buffered until the unit completes; fine for tens of thousands of rows, stream if files grow to millions.
    private sealed class Session(IFileSystem fs, string path, string sheet, ILogger log) : ISinkSession
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
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!); // path is already full
            using (var stream = fs.File.Create(path))
                stream.SaveAs(rows, sheetName: sheet, excelType: ExcelType.XLSX);
            log.Debug("Wrote {RowCount} rows to {Path}, sheet {Sheet}", rows.Count, path, sheet);
            return Task.FromResult(Result.Success());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
