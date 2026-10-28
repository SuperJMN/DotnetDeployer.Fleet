using System.Net;
using System.Net.Http.Json;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

public sealed class WorkersViewModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Connected_workers_are_listed_before_disconnected_ones()
    {
        var api = new FakeApi(
            Worker("zeta", WorkerStatus.Offline),
            Worker("rpi4", WorkerStatus.Online),
            Worker("alpha", WorkerStatus.Offline),
            Worker("pop-os", WorkerStatus.Busy));
        using var workers = Workers(api);

        await workers.RefreshCommand.Execute();

        workers.Workers.Select(w => w.Worker.Name).Should().Equal("pop-os", "rpi4", "alpha", "zeta");
    }

    [Fact]
    public async Task A_worker_that_disconnects_moves_after_the_connected_ones()
    {
        var rpi = Worker("rpi4", WorkerStatus.Online);
        var pop = Worker("pop-os", WorkerStatus.Online);
        var api = new FakeApi(rpi, pop);
        using var workers = Workers(api);
        await workers.RefreshCommand.Execute();

        api.Workers = [rpi, pop with { Status = WorkerStatus.Offline }];
        await workers.RefreshCommand.Execute();

        workers.Workers.Select(w => w.Worker.Name).Should().Equal("rpi4", "pop-os");
    }

    [Fact]
    public async Task A_worker_on_another_release_than_the_coordinator_is_flagged()
    {
        var api = new FakeApi(
            Worker("old", WorkerStatus.Online, version: "0.1.38+abc"),
            Worker("current", WorkerStatus.Online, version: "0.1.41+def"));
        using var workers = Workers(api, coordinatorVersion: "0.1.41+123");

        await workers.RefreshCommand.Execute();

        var old = workers.Workers.Single(w => w.Worker.Name == "old");
        old.VersionDiffers.Should().BeTrue();
        old.VersionMismatchMessage.Should().Contain("v0.1.38").And.Contain("v0.1.41");
        workers.Workers.Single(w => w.Worker.Name == "current").VersionDiffers.Should().BeFalse();
    }

    [Fact]
    public async Task Versions_are_compared_once_the_coordinator_reports_its_own()
    {
        var api = new FakeApi(Worker("old", WorkerStatus.Online, version: "1.0.0"));
        var snapshots = new Subject<BackendHealthSnapshot>();
        using var workers = Workers(api, snapshots: snapshots);
        await workers.RefreshCommand.Execute();
        var worker = workers.Workers.Single();
        worker.VersionDiffers.Should().BeFalse();

        snapshots.OnNext(Snapshot("0.1.41"));
        snapshots.OnNext(Snapshot(null));

        worker.VersionDiffers.Should().BeTrue();
    }

    [Fact]
    public void Save_is_only_available_when_the_disk_limit_changes()
    {
        var item = Item(new FakeApi(), Worker("rpi4", WorkerStatus.Online, maxDiskGb: 10));

        CanExecute(item.SaveConfigCommand).Should().BeFalse();

        item.MaxDiskUsageGb = 20;
        CanExecute(item.SaveConfigCommand).Should().BeTrue();

        item.MaxDiskUsageGb = 10;
        CanExecute(item.SaveConfigCommand).Should().BeFalse();
    }

    [Fact]
    public async Task Saving_stores_the_limit_and_confirms_it()
    {
        var api = new FakeApi();
        var item = Item(api, Worker("rpi4", WorkerStatus.Online, maxDiskGb: 10));
        item.MaxDiskUsageGb = 25;

        var result = await item.SaveConfigCommand.Execute();

        result.IsSuccess.Should().BeTrue();
        api.SavedDiskLimits.Should().Equal(25);
        item.IsSaved.Should().BeTrue();
        CanExecute(item.SaveConfigCommand).Should().BeFalse();
    }

    [Fact]
    public async Task Editing_the_limit_after_saving_clears_the_confirmation()
    {
        var item = Item(new FakeApi(), Worker("rpi4", WorkerStatus.Online, maxDiskGb: 10));
        item.MaxDiskUsageGb = 25;
        await item.SaveConfigCommand.Execute();

        item.MaxDiskUsageGb = 30;

        item.IsSaved.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_save_is_not_confirmed_and_can_be_retried()
    {
        var api = new FakeApi { FailSaves = true };
        var item = Item(api, Worker("rpi4", WorkerStatus.Online, maxDiskGb: 10));
        item.MaxDiskUsageGb = 25;

        var result = await item.SaveConfigCommand.Execute();

        result.IsFailure.Should().BeTrue();
        item.IsSaved.Should().BeFalse();
        CanExecute(item.SaveConfigCommand).Should().BeTrue();
    }

    [Fact]
    public void A_busy_worker_names_the_build_it_is_running()
    {
        var info = Worker("pop-os", WorkerStatus.Busy) with { CurrentJobId = Guid.NewGuid(), CurrentJobProjectName = "Zafiro" };

        var item = Item(new FakeApi(), info);

        item.CurrentJobLabel.Should().Be("Building Zafiro");
        CanExecute(item.OpenCurrentJobCommand).Should().BeTrue();
    }

    [Fact]
    public void An_idle_worker_has_no_build_to_open()
    {
        var item = Item(new FakeApi(), Worker("rpi4", WorkerStatus.Online));

        item.CurrentJobLabel.Should().BeNull();
        CanExecute(item.OpenCurrentJobCommand).Should().BeFalse();
    }

    [Fact]
    public async Task Opening_the_running_build_navigates_to_it()
    {
        var jobId = Guid.NewGuid();
        var api = new FakeApi(Worker("pop-os", WorkerStatus.Busy) with { CurrentJobId = jobId, CurrentJobProjectName = "Zafiro" })
        {
            Jobs = { new DeploymentJob { Id = jobId, ProjectId = Guid.NewGuid(), Status = JobStatus.Running } }
        };
        var navigator = Substitute.For<INavigator>();
        navigator.Go(Arg.Any<Func<object>>()).Returns(Task.FromResult(Result.Success(System.Reactive.Unit.Default)));
        using var workers = Workers(api, navigator: navigator);
        await workers.RefreshCommand.Execute();

        await workers.Workers.Single().OpenCurrentJobCommand.Execute();

        await navigator.Received(1).Go(Arg.Any<Func<object>>());
        api.RequestedJobs.Should().Equal(jobId);
    }

    private static bool CanExecute<TParam, TResult>(ReactiveUI.ReactiveCommand<TParam, TResult> command) =>
        command.CanExecute.FirstAsync().Wait();

    private static FleetApiClient.WorkerInfo Worker(string name, WorkerStatus status, string? version = null, double maxDiskGb = 10) =>
        new(Guid.NewGuid(), name, status, IsEmbedded: false, LastSeenAt: null,
            MaxDiskUsageGb: maxDiskGb, RepoStoragePath: null, Version: version);

    private static BackendHealthSnapshot Snapshot(string? version) =>
        new(version is null ? BackendHealth.Unreachable : BackendHealth.Healthy, null, DateTimeOffset.UtcNow, version, null, null);

    private static WorkerItemViewModel Item(FakeApi api, FleetApiClient.WorkerInfo info) =>
        new(info, api.Client, Workers(api), Substitute.For<INotificationService>());

    private static WorkersViewModel Workers(
        FakeApi api,
        string? coordinatorVersion = null,
        IObservable<BackendHealthSnapshot>? snapshots = null,
        INavigator? navigator = null)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(_ => Task.FromResult(Maybe.From(Result.Success(api.Client))));

        var health = Substitute.For<IBackendHealthMonitor>();
        health.Current.Returns(Snapshot(coordinatorVersion));
        health.Snapshots.Returns(snapshots ?? Observable.Never<BackendHealthSnapshot>());

        return new WorkersViewModel(context, Substitute.For<IDialog>(), Substitute.For<INotificationService>(),
            health, navigator ?? Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), ImmediateScheduler.Instance);
    }

    private sealed class FakeApi
    {
        public FakeApi(params FleetApiClient.WorkerInfo[] workers)
        {
            Workers = workers;
            var handler = new StubHandler(Handle);
            Client = new FleetApiClient(handler, handler);
            Client.SetBaseAddress("http://localhost:5000");
        }

        public FleetApiClient Client { get; }
        public IReadOnlyList<FleetApiClient.WorkerInfo> Workers { get; set; }
        public List<DeploymentJob> Jobs { get; } = [];
        public List<double> SavedDiskLimits { get; } = [];
        public List<Guid> RequestedJobs { get; } = [];
        public bool FailSaves { get; init; }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/api/workers")
                return Json(Workers);

            if (request.Method == HttpMethod.Put && path.EndsWith("/config", StringComparison.Ordinal))
            {
                if (FailSaves)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);

                var body = request.Content!.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
                SavedDiskLimits.Add(body.GetProperty("maxDiskUsageGb").GetDouble());
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/jobs/", StringComparison.Ordinal))
            {
                var id = Guid.Parse(path["/api/jobs/".Length..]);
                RequestedJobs.Add(id);
                var job = Jobs.SingleOrDefault(j => j.Id == id);
                return job is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(job);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: JsonOptions) };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
