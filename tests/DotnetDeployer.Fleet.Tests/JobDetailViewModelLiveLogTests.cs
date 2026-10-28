using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Logging;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.App.Views.Logging;
using ReactiveUI.Builder;

namespace DotnetDeployer.Fleet.Tests;

public sealed class JobDetailViewModelLiveLogTests
{
    static JobDetailViewModelLiveLogTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task Streamed_lines_appear_once_in_the_terminal_and_respect_the_severity_filter()
    {
        var job = new DeploymentJob { Id = Guid.NewGuid(), Status = JobStatus.Running };
        var stream = new Pipe();
        using var vm = CreateViewModel(job, stream.Reader.AsStream());
        var terminal = new FakeTerminal();
        vm.ShowLogsIn(terminal.Write);
        vm.MinSeverity = LogSeverity.Warning;

        await SendLine(stream, "INFO restoring packages");
        await SendLine(stream, "WARNING disk almost full");
        await SendLine(stream, "ERROR build broke");
        await stream.Writer.CompleteAsync();
        await Eventually(() => vm.FilteredLogs.Count == 2);

        terminal.Lines.Should().Equal("WARNING disk almost full", "ERROR build broke");
    }

    private static async Task SendLine(Pipe stream, string line)
    {
        await stream.Writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {line}\n\n"));
        // Live jobs deliver lines a little apart, so each one arrives in its own small batch.
        await Task.Delay(150);
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

    private static JobDetailViewModel CreateViewModel(DeploymentJob job, Stream logStream)
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/api/jobs/{job.Id}/logs")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(logStream) };

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
            fileSystemPicker: Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            notificationService: Substitute.For<Zafiro.UI.INotificationService>());
    }

    /// <summary>
    /// Records what a terminal would show: written lines, wiped by a clear-screen sequence.
    /// </summary>
    private sealed class FakeTerminal
    {
        private readonly List<string> lines = [];
        private readonly Lock gate = new();

        public IReadOnlyList<string> Lines
        {
            get { lock (gate) return lines.ToList(); }
        }

        public void Write(string text)
        {
            lock (gate)
            {
                if (text == LogTerminalFeed.ClearScreen)
                {
                    lines.Clear();
                    return;
                }

                lines.Add(StripAnsi(text).TrimEnd('\r', '\n'));
            }
        }

        private static string StripAnsi(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;]*m", "");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
