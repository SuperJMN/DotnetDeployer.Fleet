using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Every list section tells "still loading" apart from "loaded and empty", so none of them shows
/// an empty area without saying why.
/// </summary>
public sealed class ListLoadingAndEmptyStateTests
{
    [Fact]
    public void Projects_count_as_loading_before_the_first_load()
    {
        using var vm = Projects(new FakeCoordinator());

        vm.ProjectsStatus.IsLoadingFirstItems.Should().BeTrue();
        vm.ProjectsStatus.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Projects_show_loading_while_the_coordinator_has_not_answered()
    {
        var gate = new GatedHandler(new FakeCoordinator());
        using var vm = Projects(Connected(gate.CreateClient()));

        var load = vm.RefreshCommand.Execute().ToTask();
        await gate.RequestArrived;

        vm.ProjectsStatus.IsLoadingFirstItems.Should().BeTrue();
        vm.ProjectsStatus.IsEmpty.Should().BeFalse();

        gate.Release();
        await load;
    }

    [Fact]
    public async Task Projects_show_the_empty_state_once_loaded_without_projects()
    {
        using var vm = Projects(Connected(new FakeCoordinator().CreateClient()));

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.ProjectsStatus.IsEmpty.Should().BeTrue();
        vm.ProjectsStatus.IsLoadingFirstItems.Should().BeFalse();
    }

    [Fact]
    public async Task Projects_show_neither_state_when_there_are_projects()
    {
        var coordinator = new FakeCoordinator();
        coordinator.AddProject("Fleet");
        using var vm = Projects(Connected(coordinator.CreateClient()));

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.ProjectsStatus.IsEmpty.Should().BeFalse();
        vm.ProjectsStatus.IsLoadingFirstItems.Should().BeFalse();
    }

    [Fact]
    public async Task Projects_stop_showing_loading_when_the_connection_fails()
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Failure<FleetApiClient>("Cannot connect"))));
        using var vm = Projects(context);

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.ProjectsStatus.IsLoadingFirstItems.Should().BeFalse();
        vm.ProjectsStatus.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Builds_show_loading_while_the_coordinator_has_not_answered()
    {
        var gate = new GatedHandler(new FakeCoordinator());
        using var vm = Builds(Connected(gate.CreateClient()));

        var load = vm.RefreshCommand.Execute().ToTask();
        await gate.RequestArrived;

        vm.BuildsStatus.IsLoadingFirstItems.Should().BeTrue();
        vm.BuildsStatus.IsEmpty.Should().BeFalse();

        gate.Release();
        await load;
    }

    [Fact]
    public async Task Builds_show_the_empty_state_once_loaded_without_builds()
    {
        using var vm = Builds(Connected(new FakeCoordinator().CreateClient()));

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.BuildsStatus.IsEmpty.Should().BeTrue();
        vm.BuildsStatus.IsLoadingFirstItems.Should().BeFalse();
    }

    [Fact]
    public async Task Builds_show_neither_state_when_there_are_builds()
    {
        var coordinator = new FakeCoordinator();
        coordinator.AddJob(coordinator.AddProject("Fleet"), JobStatus.Succeeded);
        using var vm = Builds(Connected(coordinator.CreateClient()));

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.BuildsStatus.IsEmpty.Should().BeFalse();
        vm.BuildsStatus.IsLoadingFirstItems.Should().BeFalse();
    }

    [Fact]
    public async Task Workers_show_the_empty_state_once_loaded_without_workers()
    {
        using var vm = Workers(Connected(new FakeCoordinator().CreateClient()));

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.WorkersStatus.IsEmpty.Should().BeTrue();
        vm.WorkersStatus.IsLoadingFirstItems.Should().BeFalse();
    }

    [Fact]
    public async Task Workers_show_neither_state_when_a_worker_is_connected()
    {
        var coordinator = new FakeCoordinator();
        coordinator.Workers.Add(new FleetApiClient.WorkerInfo(Guid.NewGuid(), "rpi4", WorkerStatus.Online, false, DateTimeOffset.UtcNow, 20, null));
        using var vm = Workers(Connected(coordinator.CreateClient()));

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.WorkersStatus.IsEmpty.Should().BeFalse();
        vm.WorkersStatus.IsLoadingFirstItems.Should().BeFalse();
    }

    [Fact]
    public async Task Secrets_show_the_empty_state_once_loaded_without_secrets()
    {
        var client = new FakeCoordinator().CreateClient();
        using var vm = Secrets(client);

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.SecretsStatus.IsEmpty.Should().BeTrue();
        vm.SecretsStatus.IsLoadingFirstItems.Should().BeFalse();
    }

    [Fact]
    public async Task Secrets_leave_the_empty_state_when_one_is_added()
    {
        var client = new FakeCoordinator().CreateClient();
        using var vm = Secrets(client);
        await vm.RefreshCommand.Execute().FirstAsync();

        vm.NewName = "GITHUB_TOKEN";
        vm.NewValue = "secret";
        await vm.AddCommand.Execute().FirstAsync();

        vm.SecretsStatus.IsEmpty.Should().BeFalse();
    }

    private static ProjectsViewModel Projects(IConnectedFleetClientContext context) =>
        new(context, Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(),
            Substitute.For<INotificationService>(), new InMemoryPackageTargetSelectionStore(), new InMemorySettingsService());

    private static ProjectsViewModel Projects(FakeCoordinator coordinator) => Projects(Connected(coordinator.CreateClient()));

    private static BuildsViewModel Builds(IConnectedFleetClientContext context) =>
        new(context, Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(),
            Substitute.For<INotificationService>());

    private static WorkersViewModel Workers(IConnectedFleetClientContext context)
    {
        var health = Substitute.For<IBackendHealthMonitor>();
        health.Snapshots.Returns(Observable.Never<BackendHealthSnapshot>());
        return new WorkersViewModel(context, Substitute.For<IDialog>(), Substitute.For<INotificationService>(), health,
            Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>());
    }

    private static SecretsViewModel Secrets(FleetApiClient client) =>
        new(client, Connected(client), Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(),
            Substitute.For<INotificationService>());

    private static IConnectedFleetClientContext Connected(FleetApiClient client)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(_ => Task.FromResult(Maybe.From(Result.Success(client))));
        return context;
    }
}

/// <summary>
/// Holds every request to the coordinator until released, so a test can look at a view model
/// while its load is still running.
/// </summary>
internal sealed class GatedHandler(HttpMessageHandler coordinator) : DelegatingHandler(coordinator)
{
    private readonly TaskCompletionSource requestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task RequestArrived => requestArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public void Release() => released.TrySetResult();

    public FleetApiClient CreateClient()
    {
        var client = new FleetApiClient(this, this);
        client.SetBaseAddress("http://fleet.test:5000");
        client.SetToken("test-token");
        return client;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        requestArrived.TrySetResult();
        await released.Task.WaitAsync(cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
