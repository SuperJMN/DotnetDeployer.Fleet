using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public class JobPhaseTreeTests
{
    [Fact]
    public void Refresh_WhenPhaseUpdates_ShouldPreserveRowIdentity()
    {
        using var tree = new JobPhaseTree(name => name);
        var phase = Phase("worker.deployer.invoke", startedAt: DateTimeOffset.UtcNow);

        tree.Refresh([phase]);
        var container = tree.Phases.Single();
        var row = container.Content;

        tree.Refresh([
            Phase(
                phase.Name,
                phase.StartedAt,
                endedAt: phase.StartedAt.AddMilliseconds(1200),
                status: PhaseStatus.Ok,
                durationMs: 1200,
                id: phase.Id)
        ]);

        tree.Phases.Single().Should().BeSameAs(container);
        tree.Phases.Single().Content.Should().BeSameAs(row);
        row.Icon.Source.Should().Be("mdi-check-circle");
        row.Tone.Should().Be(Tone.Success);
        row.DurationText.Should().Be("0:01");
    }

    [Fact]
    public void Refresh_WhenNestedPhaseUpdates_ShouldPreserveNestedRowIdentity()
    {
        using var tree = new JobPhaseTree(name => name);
        var startedAt = DateTimeOffset.UtcNow;
        var parent = Phase("worker.deployer.invoke", startedAt);
        var child = Phase("nuget.pack", startedAt.AddSeconds(1));

        tree.Refresh([parent, child]);
        var childContainer = tree.Phases.Single().Content.Children.Single();
        var childRow = childContainer.Content;

        tree.Refresh([
            parent,
            Phase(
                child.Name,
                child.StartedAt,
                endedAt: child.StartedAt.AddMilliseconds(80),
                status: PhaseStatus.Ok,
                durationMs: 80,
                id: child.Id)
        ]);

        tree.Phases.Single().Content.Children.Single().Should().BeSameAs(childContainer);
        tree.Phases.Single().Content.Children.Single().Content.Should().BeSameAs(childRow);
        childRow.Icon.Source.Should().Be("mdi-check-circle");
        childRow.Tone.Should().Be(Tone.Success);
        childRow.DurationText.Should().Be("0:00");
    }

    [Fact]
    public void Refresh_WhenPhaseFails_ShouldShowItAsAFailure()
    {
        using var tree = new JobPhaseTree(name => name);
        var phase = Phase("worker.deployer.invoke", startedAt: DateTimeOffset.UtcNow);
        tree.Refresh([phase]);
        var row = tree.Phases.Single().Content;

        tree.Refresh([
            Phase(
                phase.Name,
                phase.StartedAt,
                endedAt: phase.StartedAt.AddSeconds(3),
                status: PhaseStatus.Fail,
                durationMs: 3000,
                id: phase.Id)
        ]);

        row.Icon.Source.Should().Be("mdi-close-circle");
        row.Tone.Should().Be(Tone.Danger);
    }

    [Fact]
    public void Refresh_WhenPhaseIsInFlight_ShouldShowItAsInProgress()
    {
        using var tree = new JobPhaseTree(name => name);

        tree.Refresh([Phase("worker.deployer.invoke", startedAt: DateTimeOffset.UtcNow)]);

        var row = tree.Phases.Single().Content;
        row.Icon.Source.Should().Be("mdi-progress-clock");
        row.Tone.Should().Be(Tone.Info);
    }

    private static JobPhase Phase(
        string name,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt = null,
        PhaseStatus status = PhaseStatus.Unknown,
        long? durationMs = null,
        Guid? id = null)
    {
        return new JobPhase
        {
            Id = id ?? Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            Name = name,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Status = status,
            DurationMs = durationMs
        };
    }
}
