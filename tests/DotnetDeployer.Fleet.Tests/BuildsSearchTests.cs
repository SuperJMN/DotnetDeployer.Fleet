using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>The Builds list can be searched by project, version or status.</summary>
public sealed class BuildsSearchTests
{
    [Fact]
    public async Task Searching_by_project_keeps_only_its_builds()
    {
        var coordinator = new FakeCoordinator();
        coordinator.AddJob(coordinator.AddProject("Fleet"), JobStatus.Succeeded);
        coordinator.AddJob(coordinator.AddProject("Website"), JobStatus.Succeeded);
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SearchText = "fleet";

        vm.FilteredBuilds.Select(build => build.ProjectName).Should().Equal("Fleet");
    }

    [Fact]
    public async Task Searching_by_version_finds_the_build_that_published_it()
    {
        var coordinator = new FakeCoordinator();
        var project = coordinator.AddProject("Fleet");
        coordinator.AddJob(project, JobStatus.Succeeded).Version = "2.0.1";
        coordinator.AddJob(project, JobStatus.Succeeded).Version = "1.9.0";
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SearchText = "2.0";

        vm.FilteredBuilds.Select(build => build.DisplayName).Should().Equal("2.0.1");
    }

    [Fact]
    public async Task Searching_by_status_finds_the_failed_builds()
    {
        var coordinator = new FakeCoordinator();
        var project = coordinator.AddProject("Fleet");
        var failed = coordinator.AddJob(project, JobStatus.Failed);
        coordinator.AddJob(project, JobStatus.Succeeded);
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SearchText = "failed";

        vm.FilteredBuilds.Select(build => build.Job.Id).Should().Equal(failed.Id);
    }

    [Fact]
    public async Task Matching_builds_stay_newest_first()
    {
        var coordinator = new FakeCoordinator();
        var project = coordinator.AddProject("Fleet");
        var older = coordinator.AddJob(project, JobStatus.Succeeded);
        var newer = coordinator.AddJob(project, JobStatus.Succeeded);
        older.EnqueuedAt = DateTimeOffset.UtcNow.AddHours(-2);
        newer.EnqueuedAt = DateTimeOffset.UtcNow.AddHours(-1);
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SearchText = "fleet";

        vm.FilteredBuilds.Select(build => build.Job.Id).Should().Equal(newer.Id, older.Id);
    }

    [Fact]
    public async Task A_search_that_matches_nothing_says_so()
    {
        var coordinator = new FakeCoordinator();
        coordinator.AddJob(coordinator.AddProject("Fleet"), JobStatus.Succeeded);
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SearchText = "nothing like this";

        vm.FilteredBuilds.Should().BeEmpty();
        vm.HasNoMatches.Should().BeTrue();
        vm.BuildsStatus.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task A_build_that_stops_matching_the_search_leaves_the_results_on_the_next_refresh()
    {
        var coordinator = new FakeCoordinator();
        var project = coordinator.AddProject("Fleet");
        var running = coordinator.AddJob(project, JobStatus.Running);
        coordinator.AddJob(project, JobStatus.Succeeded);
        using var vm = Builds(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();
        vm.SearchText = "running";

        running.Status = JobStatus.Failed;
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.FilteredBuilds.Should().BeEmpty();
    }

    private static BuildsViewModel Builds(FakeCoordinator coordinator)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        var client = coordinator.CreateClient();
        context.Require().Returns(_ => Task.FromResult(Maybe.From(Result.Success(client))));
        return new BuildsViewModel(context, Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(),
            Substitute.For<IDialog>(), Substitute.For<INotificationService>());
    }
}
