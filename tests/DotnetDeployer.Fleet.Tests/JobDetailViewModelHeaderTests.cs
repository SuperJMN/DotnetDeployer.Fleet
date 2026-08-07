using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.App.ViewModels;
using ReactiveUI.Builder;

namespace DotnetDeployer.Fleet.Tests;

public sealed class JobDetailViewModelHeaderTests
{
    static JobDetailViewModelHeaderTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

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

    private static JobDetailViewModel CreateViewModel(DeploymentJob job)
    {
        var client = CreateClient(job);
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

    private static FleetApiClient CreateClient(DeploymentJob job)
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
