namespace Taskus.Pipeline;

public sealed record RowError(int Row, string Message);

public sealed class UnitReport(string name)
{
    public string Name { get; } = name;
    public int Read { get; set; }
    public int Written { get; set; }
    public bool Completed { get; set; }
    public string? Failure { get; set; }
    public List<RowError> Errors { get; } = [];

    public bool HasErrors => Failure is not null || Errors.Count > 0 || !Completed;
}

public sealed class RunReport(string task)
{
    public string Task { get; } = task;
    public DateTime Started { get; } = DateTime.Now;
    public DateTime Finished { get; set; }

    /// <summary>A failure outside any unit, e.g. the input folder does not exist.</summary>
    public string? Failure { get; set; }

    public List<UnitReport> Units { get; } = [];

    public bool HasErrors => Failure is not null || Units.Any(u => u.HasErrors);

    /// <summary>Rewrites every failure and error text, e.g. to mask secrets.</summary>
    public void Redact(Func<string, string> redact)
    {
        Failure = Failure is null ? null : redact(Failure);
        foreach (var unit in Units)
        {
            unit.Failure = unit.Failure is null ? null : redact(unit.Failure);
            for (var i = 0; i < unit.Errors.Count; i++)
                unit.Errors[i] = unit.Errors[i] with { Message = redact(unit.Errors[i].Message) };
        }
    }
}
