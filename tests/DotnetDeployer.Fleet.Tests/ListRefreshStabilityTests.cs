using System.Collections.Specialized;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Refreshing a list keeps its rows: unchanged data leaves the shown collection alone, and a
/// changed row is updated in place, only moving when its position in the order really changes.
/// Tables rebuild every row (and lose the selection) when their collection is reset or replaced.
/// </summary>
public sealed class ListRefreshStabilityTests
{
    // More rows than DynamicData's default reset threshold (25).
    private const int ManyRows = 40;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Refreshing_unchanged_projects_leaves_the_project_list_untouched()
    {
        var coordinator = ManyDeployedProjects();
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();
        var changes = Record(vm.Projects);

        await vm.RefreshCommand.Execute().FirstAsync();

        changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_deploy_only_moves_its_project_row_to_the_top()
    {
        var coordinator = ManyDeployedProjects();
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();
        var oldest = coordinator.Projects.MinBy(project => project.LastDeployFinishedAt)!;
        var row = vm.Projects.Single(project => project.Project.Id == oldest.Id);
        var changes = Record(vm.Projects);

        oldest.LastDeployFinishedAt = Now;
        oldest.LastDeployVersion = "9.9.9";
        await vm.RefreshCommand.Execute().FirstAsync();

        changes.Select(change => change.Action).Should().Equal(NotifyCollectionChangedAction.Move);
        vm.Projects[0].Should().BeSameAs(row);
        row.LastDeployText.Should().EndWith("9.9.9");
    }

    [Fact]
    public async Task A_project_change_that_keeps_its_place_updates_the_row_without_touching_the_list()
    {
        var coordinator = ManyDeployedProjects();
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();
        var project = coordinator.Projects[10];
        var row = vm.Projects.Single(candidate => candidate.Project.Id == project.Id);
        var changes = Record(vm.Projects);

        project.LastDeployStatus = JobStatus.Failed;
        await vm.RefreshCommand.Execute().FirstAsync();

        changes.Should().BeEmpty();
        row.LastDeployStatusText.Should().Be("Failed");
    }

    [Fact]
    public async Task Refreshing_unchanged_builds_leaves_the_builds_list_untouched()
    {
        var coordinator = ManyBuilds();
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();
        var changes = Record(vm.FilteredBuilds);

        await vm.RefreshCommand.Execute().FirstAsync();

        changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_build_finishing_updates_its_row_without_touching_the_builds_list()
    {
        var coordinator = ManyBuilds();
        var job = coordinator.Jobs[5];
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();
        var row = vm.FilteredBuilds.Single(build => build.Job.Id == job.Id);
        var changes = Record(vm.FilteredBuilds);

        job.Status = JobStatus.Succeeded;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await vm.RefreshCommand.Execute().FirstAsync();

        changes.Should().BeEmpty();
        row.StatusText.Should().Be("Succeeded");
    }

    [Fact]
    public async Task Refreshing_unchanged_jobs_leaves_the_project_job_history_untouched()
    {
        var coordinator = ManyBuilds();
        var project = coordinator.Projects[0];
        using var vm = new ProjectDetailViewModel(project, coordinator.CreateClient(), Connected(coordinator.CreateClient()),
            Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(),
            Substitute.For<INotificationService>(), new InMemoryPackageTargetSelectionStore());
        await vm.RefreshCommand.Execute().FirstAsync();
        var changes = Record(vm.Jobs);

        await vm.RefreshCommand.Execute().FirstAsync();

        changes.Should().BeEmpty();
    }

    private static FakeCoordinator ManyDeployedProjects()
    {
        var coordinator = new FakeCoordinator();
        for (var i = 0; i < ManyRows; i++)
        {
            var project = coordinator.AddProject($"Project {i:D2}");
            project.LastDeployFinishedAt = Now.AddHours(-i - 1);
            project.LastActivityAt = project.LastDeployFinishedAt;
            project.LastDeployStatus = JobStatus.Succeeded;
            project.LastDeployVersion = $"1.0.{i}";
        }

        return coordinator;
    }

    private static FakeCoordinator ManyBuilds()
    {
        var coordinator = new FakeCoordinator();
        var project = coordinator.AddProject("Fleet");
        for (var i = 0; i < ManyRows; i++)
            coordinator.AddJob(project, JobStatus.Running);

        return coordinator;
    }

    private static List<NotifyCollectionChangedEventArgs> Record(INotifyCollectionChanged collection)
    {
        var changes = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, change) => changes.Add(change);
        return changes;
    }

    private static ProjectsViewModel Projects(FakeCoordinator coordinator) =>
        new(Connected(coordinator.CreateClient()), Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(),
            Substitute.For<IDialog>(), Substitute.For<INotificationService>(), new InMemoryPackageTargetSelectionStore(),
            new InMemorySettingsService(), timeProvider: new FixedTimeProvider(Now));

    private static BuildsViewModel Builds(FakeCoordinator coordinator) =>
        new(Connected(coordinator.CreateClient()), Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(),
            Substitute.For<IDialog>(), Substitute.For<INotificationService>());

    private static IConnectedFleetClientContext Connected(FleetApiClient client)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(_ => Task.FromResult(Maybe.From(Result.Success(client))));
        return context;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
