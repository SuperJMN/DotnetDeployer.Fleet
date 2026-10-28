using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Text;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Logging;
using ReactiveUI.Builder;
using Zafiro.UI;

namespace DotnetDeployer.Fleet.Tests;

public sealed class JobDetailViewModelLogToolbarTests
{
    private static readonly string[] Log =
    [
        "Cloning repository",
        "INFO restoring packages",
        "WARNING disk almost full",
        "ERROR build broke"
    ];

    static JobDetailViewModelLogToolbarTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task The_filter_starts_on_all_lines_and_shows_lines_without_a_severity()
    {
        using var vm = CreateViewModel(new FakeClipboard(), Substitute.For<INotificationService>());

        await Eventually(() => vm.FilteredLogs.Count == Log.Length);

        vm.SeverityFilters.Single(option => option.Minimum == vm.MinSeverity).Label.Should().Be("All lines");
        vm.FilteredLogs.Select(line => line.Text).Should().Equal(Log);
    }

    [Fact]
    public async Task Copy_all_copies_every_line_even_those_the_filter_hides_and_says_how_many()
    {
        var clipboard = new FakeClipboard();
        using var vm = CreateViewModel(clipboard, Substitute.For<INotificationService>());
        await Eventually(() => vm.FilteredLogs.Count == Log.Length);
        vm.MinSeverity = LogSeverity.Error;
        await Eventually(() => vm.FilteredLogs.Count == 1);

        await vm.CopyLogsCommand.Execute();

        clipboard.Text.Should().Be(string.Join(Environment.NewLine, Log));
        vm.CopyResultText.Should().Be("Copied 4 lines");
    }

    [Fact]
    public async Task Copying_where_there_is_no_clipboard_tells_the_user_instead_of_doing_nothing()
    {
        var notifications = Substitute.For<INotificationService>();
        using var vm = CreateViewModel(new FakeClipboard(available: false), notifications);
        await Eventually(() => vm.FilteredLogs.Count == Log.Length);

        await vm.CopyLogsCommand.Execute().Catch(Observable.Return(System.Reactive.Unit.Default));

        await notifications.Received(1).Show("No clipboard here", Maybe.From("Cannot copy logs"));
        vm.CopyResultText.Should().BeEmpty();
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private static JobDetailViewModel CreateViewModel(ITextClipboard clipboard, INotificationService notifications)
    {
        var job = new DeploymentJob { Id = Guid.NewGuid(), Status = JobStatus.Succeeded };
        var events = string.Concat(Log.Select(line => $"data: {line}\n\n"));
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/api/jobs/{job.Id}/logs")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8) };

            if (path == $"/api/jobs/{job.Id}")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(job) };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");

        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(client))));

        return new JobDetailViewModel(
            job,
            client,
            context,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            fileSystemPicker: Substitute.For<IFileSystemPicker>(),
            notificationService: notifications,
            clipboard: clipboard);
    }

    private sealed class FakeClipboard(bool available = true) : ITextClipboard
    {
        public string? Text { get; private set; }

        public Task<Result> SetText(string text)
        {
            if (!available)
                return Task.FromResult(Result.Failure("No clipboard here"));

            Text = text;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
