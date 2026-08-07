using System.Diagnostics;
using DotnetDeployer.Fleet.WorkerService.Execution;

namespace DotnetDeployer.Fleet.Tests;

public sealed class StreamingProcessRunnerTests
{
    [Fact]
    public async Task Streams_stdout_and_stderr_through_the_ordered_process_boundary()
    {
        var process = new ControllableProcess(exitCode: 3)
        {
            OutputOnStart = "standard output",
            ErrorOnStart = "standard error"
        };
        var runner = new StreamingProcessRunner(new ControllableProcessFactory(process));
        var lines = new List<string>();

        var exitCode = await runner.RunAsync(
            new ProcessStartInfo("dotnet"),
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            });

        exitCode.Should().Be(3);
        lines.Should().Equal("standard output", "[ERR] standard error");
        process.OutputReadStarted.Should().BeTrue();
        process.ErrorReadStarted.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_terminates_the_entire_process_tree()
    {
        var process = new ControllableProcess(exitCode: 0) { WaitUntilCancelled = true };
        var runner = new StreamingProcessRunner(new ControllableProcessFactory(process));
        using var cts = new CancellationTokenSource();

        var running = runner.RunAsync(
            new ProcessStartInfo("dotnet"),
            _ => Task.CompletedTask,
            cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        process.KillCalled.Should().BeTrue();
        process.KilledEntireProcessTree.Should().BeTrue();
    }

    private sealed class ControllableProcessFactory(ControllableProcess process) : IWorkerProcessFactory
    {
        public IWorkerProcess Create(ProcessStartInfo startInfo)
        {
            process.StartInfo = startInfo;
            return process;
        }
    }

    private sealed class ControllableProcess(int exitCode) : IWorkerProcess
    {
        public event Action<string> OutputLine = delegate { };
        public event Action<string> ErrorLine = delegate { };

        public ProcessStartInfo? StartInfo { get; set; }
        public string? OutputOnStart { get; init; }
        public string? ErrorOnStart { get; init; }
        public bool WaitUntilCancelled { get; init; }
        public bool OutputReadStarted { get; private set; }
        public bool ErrorReadStarted { get; private set; }
        public bool KillCalled { get; private set; }
        public bool KilledEntireProcessTree { get; private set; }
        public int ExitCode { get; } = exitCode;

        public void Start()
        {
            if (OutputOnStart is not null)
                OutputLine(OutputOnStart);
            if (ErrorOnStart is not null)
                ErrorLine(ErrorOnStart);
        }

        public void BeginOutputReadLine() => OutputReadStarted = true;
        public void BeginErrorReadLine() => ErrorReadStarted = true;

        public Task WaitForExitAsync(CancellationToken ct) => WaitUntilCancelled
            ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
            : Task.CompletedTask;

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KilledEntireProcessTree = entireProcessTree;
        }

        public void Dispose()
        {
        }
    }
}
