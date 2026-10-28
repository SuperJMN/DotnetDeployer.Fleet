using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// The phase panel of a finished job must not claim there are no phase events while they are
/// still being fetched.
/// </summary>
public sealed class JobDetailViewModelPhaseLoadingTests
{
    [Fact]
    public async Task Phases_show_loading_while_the_coordinator_has_not_answered()
    {
        var coordinator = new FakeCoordinator();
        var job = coordinator.AddJob(coordinator.AddProject("Fleet"), JobStatus.Succeeded);
        coordinator.Phases.Add(Phase(job));
        var gate = new GatedHandler(coordinator);
        using var vm = JobDetail(job, gate.CreateClient());

        var load = vm.RefreshCommand.Execute().ToTask();
        await gate.RequestArrived;

        vm.IsLoadingPhases.Should().BeTrue();
        vm.HasNoPhaseEvents.Should().BeFalse();

        gate.Release();
        await load;
    }

    [Fact]
    public async Task Phases_show_the_empty_message_only_once_loaded_without_events()
    {
        var coordinator = new FakeCoordinator();
        var job = coordinator.AddJob(coordinator.AddProject("Fleet"), JobStatus.Succeeded);
        using var vm = JobDetail(job, coordinator.CreateClient());

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.IsLoadingPhases.Should().BeFalse();
        vm.HasNoPhaseEvents.Should().BeTrue();
    }

    [Fact]
    public async Task Phases_are_listed_once_loaded()
    {
        var coordinator = new FakeCoordinator();
        var job = coordinator.AddJob(coordinator.AddProject("Fleet"), JobStatus.Succeeded);
        coordinator.Phases.Add(Phase(job));
        using var vm = JobDetail(job, coordinator.CreateClient());

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.HasPhases.Should().BeTrue();
        vm.IsLoadingPhases.Should().BeFalse();
        vm.HasNoPhaseEvents.Should().BeFalse();
    }

    [Fact]
    public async Task Phases_stop_showing_loading_when_the_connection_fails()
    {
        var coordinator = new FakeCoordinator();
        var job = coordinator.AddJob(coordinator.AddProject("Fleet"), JobStatus.Succeeded);
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Failure<FleetApiClient>("Cannot connect"))));
        using var vm = new JobDetailViewModel(job, coordinator.CreateClient(), context, Substitute.For<INavigator>(),
            notificationService: Substitute.For<INotificationService>());

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.IsLoadingPhases.Should().BeFalse();
    }

    private static JobPhase Phase(DeploymentJob job) => new()
    {
        JobId = job.Id,
        Name = "worker.solution.test",
        Status = PhaseStatus.Ok,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        EndedAt = DateTimeOffset.UtcNow
    };

    private static JobDetailViewModel JobDetail(DeploymentJob job, FleetApiClient client)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(_ => Task.FromResult(Maybe.From(Result.Success(client))));
        return new JobDetailViewModel(job, client, context, Substitute.For<INavigator>(),
            fileSystemPicker: Substitute.For<IFileSystemPicker>(),
            notificationService: Substitute.For<INotificationService>());
    }
}
