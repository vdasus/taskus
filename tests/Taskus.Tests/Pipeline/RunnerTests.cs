using FluentAssertions;
using NSubstitute;
using Serilog.Core;
using Taskus.Config;
using Taskus.Mapping;
using Taskus.Pipeline;

namespace Taskus.Tests.Pipeline;

public class RunnerTests
{
    private readonly ISink sink = Substitute.For<ISink>();
    private readonly ISinkSession session = Substitute.For<ISinkSession>();
    private readonly List<(string Unit, bool Completed)> done = [];

    public RunnerTests()
    {
        sink.OpenAsync(default!, default).ReturnsForAnyArgs(Result.Success(session));
        session.WriteAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        session.CompleteAsync(default).ReturnsForAnyArgs(Result.Success());
    }

    private SourceUnit Unit(string name, params int[] ids) => new(
        name,
        ids.Select(id => new SourceRow(id, new Dictionary<string, object?> { ["id"] = id })).ToAsyncEnumerable(),
        completed => done.Add((name, completed)));

    private void FailRow(int id) =>
        session.WriteAsync(Arg.Is<Row>(r => (int)r["id"]! == id), Arg.Any<CancellationToken>()).Returns(Result.Failure($"bad {id}"));

    private Runner Runner(ErrorPolicy onError, int maxErrors, params SourceUnit[] units)
    {
        var source = Substitute.For<ISource>();
        source.Units().Returns(units);
        return new Runner("t", source, RowMapper.Create([]).Value, sink, onError, maxErrors, Logger.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task Continue_SkipsFailedRowsAndCompletesUnit()
    {
        FailRow(2);

        var report = await Runner(ErrorPolicy.Continue, 100, Unit("a", 1, 2, 3)).RunAsync(dryRun: false, TestContext.Current.CancellationToken);

        var unit = report.Units.Single();
        unit.Should().BeEquivalentTo(new { Read = 3, Written = 2, Completed = true, Failure = (string?)null });
        unit.Errors.Should().Equal(new RowError(2, "bad 2"));
        await session.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
        done.Should().Equal(("a", true));
        report.HasErrors.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task Stop_EndsRunAtFirstError()
    {
        FailRow(2);

        var report = await Runner(ErrorPolicy.Stop, 100, Unit("a", 1, 2, 3), Unit("b", 4)).RunAsync(false, TestContext.Current.CancellationToken);

        report.Units.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Read = 2, Written = 1, Completed = false });
        await session.DidNotReceive().CompleteAsync(Arg.Any<CancellationToken>());
        await session.Received(1).DisposeAsync();
        done.Should().Equal(("a", false));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task AllOrNothing_RollsBackUnitAndGoesOnWithNextUnit()
    {
        session.AllOrNothing.Returns(true);
        FailRow(2);

        var report = await Runner(ErrorPolicy.Continue, 100, Unit("a", 1, 2, 3), Unit("b", 4)).RunAsync(false, TestContext.Current.CancellationToken);

        report.Units[0].Should().BeEquivalentTo(new { Read = 2, Written = 0, Completed = false, Failure = "rolled back" });
        report.Units[1].Should().BeEquivalentTo(new { Read = 1, Written = 1, Completed = true });
        done.Should().Equal(("a", false), ("b", true));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MaxErrors_CountsAcrossUnitsAndStopsRun()
    {
        FailRow(1);
        FailRow(3);

        var report = await Runner(ErrorPolicy.Continue, 2, Unit("a", 1, 2), Unit("b", 3, 4), Unit("c", 5)).RunAsync(false, TestContext.Current.CancellationToken);

        report.Units.Select(u => u.Name).Should().Equal("a", "b");
        report.Units[1].Read.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DryRun_NeverOpensSinkNorMovesInput()
    {
        var report = await Runner(ErrorPolicy.Stop, 100, Unit("a", 1, 2)).RunAsync(dryRun: true, TestContext.Current.CancellationToken);

        report.Units.Single().Should().BeEquivalentTo(new { Read = 2, Written = 2, Completed = true });
        await sink.DidNotReceiveWithAnyArgs().OpenAsync(default!, TestContext.Current.CancellationToken);
        done.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SinkOpenFailure_FailsUnitAndLeavesInputForNextRun()
    {
        sink.OpenAsync(default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(Result.Failure<ISinkSession>("no db"));

        var report = await Runner(ErrorPolicy.Continue, 100, Unit("a", 1), Unit("b", 2)).RunAsync(false, TestContext.Current.CancellationToken);

        report.Units.Should().HaveCount(2).And.OnlyContain(u => u.Failure == "no db" && !u.Completed && u.Read == 0);
        done.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SourceFailure_IsReportedNotThrown()
    {
        var source = Substitute.For<ISource>();
        source.Units().Returns(_ => throw new DirectoryNotFoundException("no folder"));
        var runner = new Runner("t", source, RowMapper.Create([]).Value, sink, ErrorPolicy.Stop, 100, Logger.None);

        var report = await runner.RunAsync(false, TestContext.Current.CancellationToken);

        report.Failure.Should().Be("no folder");
        report.HasErrors.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    public async Task InputNotMoved_FailsThatUnitAndGoesOn()
    {
        var locked = new SourceUnit("a", new[] { new SourceRow(1, new Dictionary<string, object?> { ["id"] = 1 }) }.ToAsyncEnumerable(),
            _ => throw new IOException("file is in use"));

        var report = await Runner(ErrorPolicy.Stop, 100, locked, Unit("b", 2)).RunAsync(false, TestContext.Current.CancellationToken);

        report.Failure.Should().BeNull();
        report.Units[0].Should().BeEquivalentTo(new { Written = 1, Completed = false });
        report.Units[0].Failure.Should().Be("input not moved, the next run reads it again: file is in use");
        done.Should().Equal(("b", true));
    }
}
