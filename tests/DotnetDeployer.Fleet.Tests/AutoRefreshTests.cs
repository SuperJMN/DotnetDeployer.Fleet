using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.Reactive.Testing;
using ReactiveUI.Builder;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

public sealed class AutoRefreshTests
{
    static AutoRefreshTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task Builds_list_shows_a_job_finishing_without_a_manual_refresh()
    {
        var job = RunningJob(Guid.NewGuid());
        var coordinator = new FakeCoordinator { Jobs = [job] };
        var scheduler = new TestScheduler();
        using var vm = new BuildsViewModel(Connected(coordinator.Client), Substitute.For<INavigator>(),
            Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(), Substitute.For<INotificationService>(), scheduler);
        await vm.RefreshCommand.Execute().FirstAsync();
        await vm.AutoRefresh.StartCommand.Execute().FirstAsync();

        coordinator.Jobs = [Finished(job, JobStatus.Succeeded)];
        scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks);

        await Eventually(() => vm.Builds.Single().StatusText == "Succeeded");
    }

    [Fact]
    public async Task Builds_list_does_not_download_project_icons_again_on_each_automatic_refresh()
    {
        var job = RunningJob(Guid.NewGuid());
        var coordinator = new FakeCoordinator { Jobs = [job] };
        var scheduler = new TestScheduler();
        using var vm = new BuildsViewModel(Connected(coordinator.Client), Substitute.For<INavigator>(),
            Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(), Substitute.For<INotificationService>(), scheduler);
        await vm.RefreshCommand.Execute().FirstAsync();
        await vm.AutoRefresh.StartCommand.Execute().FirstAsync();

        coordinator.Jobs = [Finished(job, JobStatus.Failed)];
        scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks);
        await Eventually(() => vm.Builds.Single().StatusText == "Failed");

        coordinator.Requests.Count(path => path.EndsWith("/icon")).Should().Be(1);
    }

    [Fact]
    public async Task Stopped_auto_refresh_leaves_the_builds_list_alone()
    {
        var job = RunningJob(Guid.NewGuid());
        var coordinator = new FakeCoordinator { Jobs = [job] };
        var scheduler = new TestScheduler();
        using var vm = new BuildsViewModel(Connected(coordinator.Client), Substitute.For<INavigator>(),
            Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(), Substitute.For<INotificationService>(), scheduler);
        await vm.RefreshCommand.Execute().FirstAsync();
        await vm.AutoRefresh.StartCommand.Execute().FirstAsync();
        await vm.AutoRefresh.StopCommand.Execute().FirstAsync();
        var requestsBefore = coordinator.Requests.Count;

        coordinator.Jobs = [Finished(job, JobStatus.Succeeded)];
        scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks * 3);

        coordinator.Requests.Count.Should().Be(requestsBefore);
        vm.Builds.Single().StatusText.Should().Be("Running");
    }

    [Fact]
    public async Task Auto_refresh_before_any_load_does_not_ask_for_a_connection()
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        var scheduler = new TestScheduler();
        using var vm = new WorkersViewModel(context, Substitute.For<IDialog>(), Substitute.For<INotificationService>(), QuietHealth(), Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), autoRefreshScheduler: scheduler);
        await vm.AutoRefresh.StartCommand.Execute().FirstAsync();

        scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks * 3);

        await context.DidNotReceive().Require();
    }

    [Fact]
    public async Task Project_detail_shows_a_job_finishing_without_a_manual_refresh()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Fleet", GitUrl = "https://example.test/repo.git", Branch = "master" };
        var job = RunningJob(project.Id);
        var coordinator = new FakeCoordinator { Jobs = [job], Projects = [project] };
        var scheduler = new TestScheduler();
        using var vm = new ProjectDetailViewModel(project, coordinator.Client, Connected(coordinator.Client),
            Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(),
            Substitute.For<INotificationService>(), new InMemoryPackageTargetSelectionStore(), scheduler);
        await vm.RefreshCommand.Execute().FirstAsync();
        await vm.AutoRefresh.StartCommand.Execute().FirstAsync();

        coordinator.Jobs = [Finished(job, JobStatus.Failed)];
        scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks);

        await Eventually(() => vm.Jobs.Single().StatusText == "Failed");
    }

    [Fact]
    public async Task Workers_list_shows_status_and_last_seen_changes_without_a_manual_refresh()
    {
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-5);
        var worker = new FleetApiClient.WorkerInfo(Guid.NewGuid(), "rpi4", WorkerStatus.Online, false, lastSeen, 20, null);
        var coordinator = new FakeCoordinator { Workers = [worker] };
        var scheduler = new TestScheduler();
        using var vm = new WorkersViewModel(Connected(coordinator.Client), Substitute.For<IDialog>(),
            Substitute.For<INotificationService>(), QuietHealth(), Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), autoRefreshScheduler: scheduler);
        await vm.RefreshCommand.Execute().FirstAsync();
        await vm.AutoRefresh.StartCommand.Execute().FirstAsync();

        var seenNow = DateTimeOffset.UtcNow;
        coordinator.Workers = [worker with { Status = WorkerStatus.Busy, LastSeenAt = seenNow }];
        scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks);

        await Eventually(() => vm.Workers.Single().Worker.Status == WorkerStatus.Busy);
        vm.Workers.Single().LastSeenText.Should().Be(LocalTimestampFormatter.Format(seenNow));
    }

    [Fact]
    public async Task A_failing_automatic_refresh_keeps_the_list_and_recovers_on_the_next_tick()
    {
        var worker = new FleetApiClient.WorkerInfo(Guid.NewGuid(), "rpi4", WorkerStatus.Online, false, DateTimeOffset.UtcNow, 20, null);
        var coordinator = new FakeCoordinator { Workers = [worker] };
        var notifications = Substitute.For<INotificationService>();
        var scheduler = new TestScheduler();
        using var vm = new WorkersViewModel(Connected(coordinator.Client), Substitute.For<IDialog>(), notifications, QuietHealth(), Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), autoRefreshScheduler: scheduler);
        await vm.RefreshCommand.Execute().FirstAsync();
        await vm.AutoRefresh.StartCommand.Execute().FirstAsync();

        coordinator.IsDown = true;
        scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks);
        await Eventually(() => coordinator.FailedRequests > 0);
        coordinator.IsDown = false;
        coordinator.Workers = [worker with { Status = WorkerStatus.Offline }];
        await Eventually(() =>
        {
            scheduler.AdvanceBy(AutoRefresh.DefaultPeriod.Ticks);
            return vm.Workers.Single().Worker.Status == WorkerStatus.Offline;
        });

        notifications.ReceivedCalls().Should().BeEmpty();
    }

    private static DeploymentJob RunningJob(Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Status = JobStatus.Running,
        EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private static DeploymentJob Finished(DeploymentJob job, JobStatus status) => new()
    {
        Id = job.Id,
        ProjectId = job.ProjectId,
        Status = status,
        EnqueuedAt = job.EnqueuedAt,
        StartedAt = job.StartedAt,
        FinishedAt = DateTimeOffset.UtcNow
    };

    private static IConnectedFleetClientContext Connected(FleetApiClient client)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(_ => Task.FromResult(Maybe.From(Result.Success(client))));
        return context;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The expected state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeCoordinator
    {
        private readonly object gate = new();
        private readonly List<string> requests = [];
        private int failedRequests;

        public FakeCoordinator()
        {
            var handler = new StubHandler(Respond);
            Client = new FleetApiClient(handler, handler);
            Client.SetBaseAddress("http://localhost:5000");
        }

        public FleetApiClient Client { get; }
        public volatile bool IsDown;
        public IReadOnlyList<DeploymentJob> Jobs { get; set; } = [];
        public IReadOnlyList<Project> Projects { get; set; } = [];
        public IReadOnlyList<FleetApiClient.WorkerInfo> Workers { get; set; } = [];
        public int FailedRequests => Volatile.Read(ref failedRequests);

        public IReadOnlyList<string> Requests
        {
            get { lock (gate) return requests.ToList(); }
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (gate) requests.Add(path);

            if (IsDown)
            {
                Interlocked.Increment(ref failedRequests);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var project = Projects.FirstOrDefault(p => path == $"/api/projects/{p.Id}");
            var projectJobs = Projects.FirstOrDefault(p => path == $"/api/projects/{p.Id}/jobs");

            return path switch
            {
                "/api/jobs" => Json(Jobs),
                "/api/projects" => Json(Projects),
                "/api/workers" => Json(Workers),
                _ when project is not null => Json(project),
                _ when projectJobs is not null => Json(Jobs.Where(job => job.ProjectId == projectJobs.Id)),
                _ when path.EndsWith("/icon") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }

        private static HttpResponseMessage Json<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private static IBackendHealthMonitor QuietHealth()
    {
        var health = Substitute.For<IBackendHealthMonitor>();
        health.Snapshots.Returns(Observable.Never<BackendHealthSnapshot>());
        return health;
    }
}
