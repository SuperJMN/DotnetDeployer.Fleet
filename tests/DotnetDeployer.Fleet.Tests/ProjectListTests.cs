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
/// The project list shows how each project's last deploy went and whether it is busy, and can be
/// searched and ordered by last deploy, by recent activity or by name.
/// </summary>
public sealed class ProjectListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Projects_are_ordered_by_their_last_deploy_by_default_with_never_deployed_ones_last()
    {
        var coordinator = new FakeCoordinator();
        AddProject(coordinator, "Never", lastDeploy: null);
        AddProject(coordinator, "Old", lastDeploy: Now.AddDays(-3));
        AddProject(coordinator, "Recent", lastDeploy: Now.AddHours(-1));
        using var vm = Projects(coordinator);

        await vm.RefreshCommand.Execute().FirstAsync();

        Names(vm).Should().Equal("Recent", "Old", "Never");
    }

    [Fact]
    public async Task Projects_can_be_ordered_by_name()
    {
        var coordinator = new FakeCoordinator();
        AddProject(coordinator, "charlie", lastDeploy: Now.AddHours(-1));
        AddProject(coordinator, "Alpha", lastDeploy: null);
        AddProject(coordinator, "bravo", lastDeploy: Now.AddHours(-2));
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SelectedSort = ProjectSortOption.For(ProjectSortOrder.Name);

        Names(vm).Should().Equal("Alpha", "bravo", "charlie");
    }

    [Fact]
    public async Task Ordering_by_recent_activity_puts_a_project_with_a_job_in_progress_first()
    {
        var coordinator = new FakeCoordinator();
        var busy = AddProject(coordinator, "Busy", lastDeploy: Now.AddDays(-5), lastActivity: Now.AddDays(-5));
        AddProject(coordinator, "Idle", lastDeploy: Now.AddHours(-1), lastActivity: Now.AddHours(-1));
        AddProject(coordinator, "Untouched", lastDeploy: null);
        coordinator.AddJob(busy, JobStatus.Running).EnqueuedAt = Now.AddMinutes(-2);
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SelectedSort = ProjectSortOption.For(ProjectSortOrder.RecentActivity);

        Names(vm).Should().Equal("Busy", "Idle", "Untouched");
    }

    [Fact]
    public void The_chosen_order_is_remembered_in_the_settings()
    {
        var settings = new InMemorySettingsService();
        var coordinator = new FakeCoordinator();
        using (var first = Projects(coordinator, settings))
            first.SelectedSort = ProjectSortOption.For(ProjectSortOrder.Name);

        using var second = Projects(coordinator, settings);

        second.SelectedSort.Order.Should().Be(ProjectSortOrder.Name);
    }

    [Fact]
    public async Task Searching_keeps_only_the_projects_whose_name_or_repository_contains_the_text()
    {
        var coordinator = new FakeCoordinator();
        coordinator.AddProject("Zafiro.Avalonia", "https://github.com/SuperJMN/Zafiro.Avalonia.git");
        coordinator.AddProject("Fleet", "https://github.com/SuperJMN/DotnetDeployer.Fleet.git");
        coordinator.AddProject("Website", "https://gitlab.com/acme/site.git");
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SearchText = "superjmn";

        Names(vm).Should().BeEquivalentTo("Zafiro.Avalonia", "Fleet");
        vm.HasNoMatches.Should().BeFalse();
    }

    [Fact]
    public async Task A_search_that_matches_nothing_says_so_instead_of_reporting_an_empty_fleet()
    {
        var coordinator = new FakeCoordinator();
        coordinator.AddProject("Fleet");
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SearchText = "nothing like this";

        vm.Projects.Should().BeEmpty();
        vm.HasNoMatches.Should().BeTrue();
        vm.ProjectsStatus.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Clearing_the_search_shows_every_project_again()
    {
        var coordinator = new FakeCoordinator();
        coordinator.AddProject("Fleet");
        coordinator.AddProject("Website");
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();
        vm.SearchText = "fleet";

        vm.SearchText = "";

        vm.Projects.Should().HaveCount(2);
        vm.HasNoMatches.Should().BeFalse();
    }

    [Fact]
    public async Task A_project_shows_when_its_last_deploy_finished_what_it_published_and_how_it_went()
    {
        var coordinator = new FakeCoordinator();
        var project = AddProject(coordinator, "Mcp", lastDeploy: Now.AddHours(-2).AddMinutes(-10));
        project.LastDeployVersion = "0.1.20+Branch.master.Sha.1043e50";
        project.LastDeployStatus = JobStatus.Succeeded;
        project.LastDeployNuGetVerification = NuGetVerificationStatus.Verified;
        project.LastDeployCommitSha = "1043e50aa1b2c3d4e5f6";
        using var vm = Projects(coordinator);

        await vm.RefreshCommand.Execute().FirstAsync();

        var row = vm.Projects.Single();
        row.LastDeployText.Should().Be("2 h ago · 0.1.20");
        row.LastDeployStatusText.Should().Be("Succeeded · NuGet verified");
        row.LastDeployStatusTone.Should().Be(Tone.Success);
        row.LastDeployCommitText.Should().Be("1043e50");
    }

    [Fact]
    public async Task A_project_never_deployed_says_so()
    {
        var coordinator = new FakeCoordinator();
        AddProject(coordinator, "New", lastDeploy: null);
        using var vm = Projects(coordinator);

        await vm.RefreshCommand.Execute().FirstAsync();

        var row = vm.Projects.Single();
        row.LastDeployText.Should().Be("Never deployed");
        row.HasLastDeployStatus.Should().BeFalse();
        row.LastDeployCommitText.Should().Be("--");
    }

    [Fact]
    public async Task A_project_with_a_job_in_progress_shows_it()
    {
        var coordinator = new FakeCoordinator();
        var project = coordinator.AddProject("Fleet");
        coordinator.AddJob(project, JobStatus.Succeeded);
        coordinator.AddJob(project, JobStatus.Running);
        using var vm = Projects(coordinator);

        await vm.RefreshCommand.Execute().FirstAsync();

        var row = vm.Projects.Single();
        row.HasActiveJob.Should().BeTrue();
        row.ActiveJobText.Should().Be("Running · Deploy");
    }

    [Fact]
    public async Task A_project_stops_showing_a_job_once_it_finishes()
    {
        var coordinator = new FakeCoordinator();
        var project = coordinator.AddProject("Fleet");
        var job = coordinator.AddJob(project, JobStatus.Queued);
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        job.Status = JobStatus.Failed;
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.Projects.Single().HasActiveJob.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_deploy_moves_its_project_to_the_top()
    {
        var coordinator = new FakeCoordinator();
        AddProject(coordinator, "First", lastDeploy: Now.AddHours(-1));
        var second = AddProject(coordinator, "Second", lastDeploy: Now.AddHours(-3));
        using var vm = Projects(coordinator);
        await vm.RefreshCommand.Execute().FirstAsync();

        second.LastDeployFinishedAt = Now.AddMinutes(-1);
        await vm.RefreshCommand.Execute().FirstAsync();

        Names(vm).Should().Equal("Second", "First");
    }

    private static Project AddProject(FakeCoordinator coordinator, string name, DateTimeOffset? lastDeploy, DateTimeOffset? lastActivity = null)
    {
        var project = coordinator.AddProject(name);
        project.LastDeployFinishedAt = lastDeploy;
        project.LastDeployStatus = lastDeploy is null ? null : JobStatus.Succeeded;
        project.LastActivityAt = lastActivity ?? lastDeploy;
        return project;
    }

    private static IEnumerable<string> Names(ProjectsViewModel vm) => vm.Projects.Select(project => project.Project.Name);

    private static ProjectsViewModel Projects(FakeCoordinator coordinator, ISettingsService? settings = null) =>
        new(Connected(coordinator.CreateClient()), Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(),
            Substitute.For<IDialog>(), Substitute.For<INotificationService>(), new InMemoryPackageTargetSelectionStore(),
            settings ?? new InMemorySettingsService(), timeProvider: new FixedTimeProvider(Now));

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
