using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public sealed class JobDetailViewModelHeaderTests
{
    [Fact]
    public async Task Header_ShouldSummarizeTheViewedJobAndExposeStatusActions()
    {
        var job = new DeploymentJob
        {
            Id = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789"),
            Kind = JobKind.PackageBuild,
            Status = JobStatus.Succeeded,
            Version = "2.0.1+58f1c495e00554b14859456e8557aa98b9b177dd",
            EnqueuedAt = new DateTimeOffset(2026, 5, 14, 10, 30, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 5, 14, 10, 32, 5, TimeSpan.Zero),
            TotalDurationMs = 125_000
        };
        using var vm = CreateViewModel(job);

        var header = (SectionHeader)await vm.Header.FirstAsync();

        header.Title.Should().Be("Package build 2.0.1");
        header.Subtitle.Should().Contain("Succeeded");
        header.Subtitle.Should().Contain("Elapsed: 2:05");
        header.Subtitle.Should().Contain("Queued:");
        header.Actions.Select(action => action.Text).Should().Equal("Detailed log", "Refresh");
    }

    [Fact]
    public async Task Header_shows_the_queued_time_like_the_job_lists()
    {
        var enqueuedAt = new DateTimeOffset(2026, 10, 14, 10, 54, 0, TimeSpan.Zero);
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Succeeded,
            EnqueuedAt = enqueuedAt,
            FinishedAt = enqueuedAt.AddMinutes(2)
        };
        using var vm = CreateViewModel(job);

        var header = (SectionHeader)await vm.Header.FirstAsync();

        header.Subtitle.Should().Contain($"Queued: {LocalTimestampFormatter.Format(enqueuedAt)}");
    }

    [Fact]
    public async Task Header_ShouldKeepItsButtonsWhileOnlyTheElapsedTimeChanges()
    {
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Deploy,
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        using var vm = CreateViewModel(job);
        var headers = new List<object>();
        using var subscription = vm.Header.Subscribe(header => { lock (headers) headers.Add(header); });
        var header = (SectionHeader)headers.Single();
        var initialElapsed = Elapsed(header.Subtitle);

        var elapsedChanged = await WaitUntil(() => Elapsed(header.Subtitle) != initialElapsed, TimeSpan.FromSeconds(10));

        elapsedChanged.Should().BeTrue();
        lock (headers) headers.Should().ContainSingle().Which.Should().BeSameAs(header);
    }

    [Fact]
    public async Task Header_ShouldBeRebuiltWhenItsActionsChange()
    {
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Deploy,
            Status = JobStatus.Succeeded,
            EnqueuedAt = new DateTimeOffset(2026, 5, 14, 10, 30, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 5, 14, 10, 32, 5, TimeSpan.Zero),
            TotalDurationMs = 125_000
        };
        using var vm = CreateViewModel(job);

        await vm.ToggleDetailedLogCommand.Execute().ToTask();

        var header = (SectionHeader)await vm.Header.FirstAsync();
        header.Actions.Select(action => action.Text).Should().Equal("Hide detailed log", "Refresh");
    }

    [Fact]
    public async Task Header_ShouldNameTheWorkerAndTheCommitOfTheJob()
    {
        var workerId = Guid.NewGuid();
        var job = FinishedJob(JobStatus.Failed);
        job.WorkerId = workerId;
        job.TriggerCommitSha = "58f1c495e00554b14859456e8557aa98b9b177dd";
        using var vm = CreateViewModel(job, [Worker(workerId, "rpi4")]);

        var header = await vm.Header.OfType<SectionHeader>()
            .FirstAsync(h => h.Subtitle.Contains("Worker: rpi4"))
            .Timeout(TimeSpan.FromSeconds(5));

        header.Subtitle.Should().Contain("Commit: 58f1c49");
    }

    [Fact]
    public async Task Header_WhenWorkerNamesAreUnavailable_ShouldShowTheShortWorkerId()
    {
        var job = FinishedJob(JobStatus.Failed);
        job.WorkerId = Guid.Parse("12345678-9abc-def0-1234-56789abcdef0");
        using var vm = CreateViewModel(job);

        var header = (SectionHeader)await vm.Header.FirstAsync();

        header.Subtitle.Should().Contain("Worker: 12345678");
    }

    [Fact]
    public async Task Elapsed_time_uses_minutes_and_seconds()
    {
        using var vm = CreateViewModel(FinishedJob(JobStatus.Failed));

        var header = (SectionHeader)await vm.Header.FirstAsync();

        header.Subtitle.Should().Contain("Elapsed: 4:03");
    }

    [Fact]
    public async Task Showing_the_error_opens_the_detailed_log()
    {
        using var vm = CreateViewModel(FinishedJob(JobStatus.Failed));

        await vm.ShowErrorInLogCommand.Execute();

        vm.IsDetailedLogVisible.Should().BeTrue();
        var header = (SectionHeader)await vm.Header.FirstAsync();
        header.Actions.Select(action => action.Text).Should().Contain("Hide detailed log");
    }

    [Fact]
    public void DestructiveHeaderAction_ShouldNotBeRenderedAsPrimaryOrSecondary()
    {
        var command = Substitute.For<System.Windows.Input.ICommand>();
        var action = new HeaderAction("Cancel", "mdi-stop-circle-outline", command, isDestructive: true);

        action.IsDestructiveCommand.Should().BeTrue();
        action.IsPrimaryCommand.Should().BeFalse();
        action.IsSecondaryCommand.Should().BeFalse();
    }

    [Fact]
    public void Solution_test_phase_has_a_readable_label()
    {
        JobDetailViewModel.FormatPhaseName("worker.solution.test")
            .Should().Be("Running solution tests");
    }

    private static string? Elapsed(string? subtitle) =>
        subtitle is null ? null : Regex.Match(subtitle, "Elapsed: [^|]*").Value;

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(50);
        }

        return true;
    }

    private static DeploymentJob FinishedJob(JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Kind = JobKind.Deploy,
        Status = status,
        ErrorMessage = status == JobStatus.Failed ? "dotnet test exited with code 1" : null,
        EnqueuedAt = new DateTimeOffset(2026, 5, 14, 10, 30, 0, TimeSpan.Zero),
        FinishedAt = new DateTimeOffset(2026, 5, 14, 10, 34, 3, TimeSpan.Zero),
        TotalDurationMs = 243_000
    };

    private static FleetApiClient.WorkerInfo Worker(Guid id, string name) =>
        new(id, name, WorkerStatus.Online, false, null, 10, null);

    private static JobDetailViewModel CreateViewModel(DeploymentJob job, FleetApiClient.WorkerInfo[]? workers = null)
    {
        var client = CreateClient(job, workers);
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(client))));

        return new JobDetailViewModel(
            job,
            client,
            context,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            fileSystemPicker: Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            notificationService: Substitute.For<Zafiro.UI.INotificationService>());
    }

    private static FleetApiClient CreateClient(DeploymentJob job, FleetApiClient.WorkerInfo[]? workers)
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == $"/api/jobs/{job.Id}")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(job)
                };

            if (request.Method == HttpMethod.Get && path == $"/api/jobs/{job.Id}/logs")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("")
                };

            if (request.Method == HttpMethod.Get && path == $"/api/jobs/{job.Id}/artifacts")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<PackageArtifact>())
                };

            if (request.Method == HttpMethod.Get && path == "/api/workers" && workers is not null)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(workers)
                };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");
        return client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
