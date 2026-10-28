using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Logging;
using DotnetDeployer.Fleet.App.ViewModels;
using ReactiveUI.Builder;
using XTerm;
using XTerm.Options;

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
        vm.ShowLogsIn(terminal.Write, terminal.Clear);
        vm.MinSeverity = LogSeverity.Warning;

        await SendLine(stream, "INFO restoring packages");
        await SendLine(stream, "WARNING disk almost full");
        await SendLine(stream, "ERROR build broke");
        await stream.Writer.CompleteAsync();

        await Eventually(() => terminal.Lines.Count == 2);
        terminal.Lines.Should().Equal("WARNING disk almost full", "ERROR build broke");
    }

    [Fact]
    public async Task A_large_burst_after_earlier_lines_shows_every_line_once()
    {
        var job = new DeploymentJob { Id = Guid.NewGuid(), Status = JobStatus.Running };
        var stream = new Pipe();
        using var vm = CreateViewModel(job, stream.Reader.AsStream());
        var terminal = new FakeTerminal();
        vm.ShowLogsIn(terminal.Write, terminal.Clear);
        var burst = Enumerable.Range(1, 60).Select(i => $"compiling file {i:000}").ToList();

        await SendLine(stream, "first line");
        await SendLine(stream, "second line");
        await SendLines(stream, burst);
        await SendLine(stream, "after the burst");
        await SendLine(stream, "last line");
        await stream.Writer.CompleteAsync();

        string[] expected = ["first line", "second line", .. burst, "after the burst", "last line"];
        await Eventually(() => terminal.Lines.Count >= expected.Length);
        terminal.Lines.Should().Equal(expected);
    }

    [Fact]
    public async Task Raising_the_severity_while_streaming_removes_the_hidden_lines_from_the_terminal()
    {
        var job = new DeploymentJob { Id = Guid.NewGuid(), Status = JobStatus.Running };
        var stream = new Pipe();
        using var vm = CreateViewModel(job, stream.Reader.AsStream());
        var terminal = new FakeTerminal();
        vm.ShowLogsIn(terminal.Write, terminal.Clear);

        await SendLine(stream, "INFO restoring packages");
        await SendLine(stream, "WARNING disk almost full");
        await SendLine(stream, "INFO building");
        await Eventually(() => terminal.Lines.Count == 3);

        vm.MinSeverity = LogSeverity.Warning;
        await SendLine(stream, "INFO testing");
        await SendLine(stream, "ERROR build broke");
        await stream.Writer.CompleteAsync();

        await Eventually(() => terminal.Lines.Contains("ERROR build broke"));
        terminal.Lines.Should().Equal("WARNING disk almost full", "ERROR build broke");
        terminal.Clears.Should().Be(1);
    }

    private static async Task SendLine(Pipe stream, string line)
    {
        await stream.Writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {line}\n\n"));
        // Live jobs deliver lines a little apart, so each one arrives in its own small batch.
        await Task.Delay(150);
    }

    private static async Task SendLines(Pipe stream, IEnumerable<string> lines)
    {
        // A burst written at once is delivered as one big batch.
        var payload = string.Concat(lines.Select(line => $"data: {line}\n\n"));
        await stream.Writer.WriteAsync(Encoding.UTF8.GetBytes(payload));
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
    /// A real terminal emulator in memory. Like the terminal in the app, clearing
    /// replaces it with an empty one, because escape sequences leave the scrollback.
    /// </summary>
    private sealed class FakeTerminal
    {
        private readonly Lock gate = new();
        private Terminal terminal = NewTerminal();

        public int Clears { get; private set; }

        /// <summary>Every non-blank line the terminal holds, scrollback included.</summary>
        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (gate)
                {
                    return Enumerable.Range(0, terminal.Buffer.Length)
                        .Select(terminal.GetLine)
                        .Where(line => line.Length > 0)
                        .ToList();
                }
            }
        }

        public void Write(string text)
        {
            lock (gate) terminal.Write(text);
        }

        public void Clear()
        {
            lock (gate)
            {
                terminal = NewTerminal();
                Clears++;
            }
        }

        private static Terminal NewTerminal() =>
            new(new TerminalOptions { Cols = 200, Rows = 3, Scrollback = 10_000 });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
