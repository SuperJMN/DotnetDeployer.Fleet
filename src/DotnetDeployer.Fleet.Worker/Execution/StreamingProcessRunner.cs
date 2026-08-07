using System.Diagnostics;
using System.Threading.Channels;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal interface IStreamingProcessRunner
{
    Task<int> RunAsync(
        ProcessStartInfo startInfo,
        Func<string, Task> onLine,
        CancellationToken ct = default);
}

/// <summary>
/// Runs worker child processes with ordered, non-blocking stdout/stderr streaming.
/// Cancellation always attempts to terminate the complete process tree.
/// </summary>
internal sealed class StreamingProcessRunner : IStreamingProcessRunner
{
    internal static StreamingProcessRunner Instance { get; } = new(new SystemWorkerProcessFactory());

    private readonly IWorkerProcessFactory processFactory;

    internal StreamingProcessRunner(IWorkerProcessFactory processFactory)
    {
        this.processFactory = processFactory;
    }

    public async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        Func<string, Task> onLine,
        CancellationToken ct = default)
    {
        using var process = processFactory.Create(startInfo);

        // Process events must remain cheap: enqueue immediately, then let one
        // consumer deliver logs in order without starving worker heartbeats.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        process.OutputLine += line => channel.Writer.TryWrite(line);
        process.ErrorLine += line => channel.Writer.TryWrite($"[ERR] {line}");

        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try { await onLine(line).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* transient log delivery must not tear down the command */ }
                }
            }
            catch (OperationCanceledException) { /* expected on cancellation */ }
        });

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            channel.Writer.TryComplete();
            try { await consumer.ConfigureAwait(false); } catch { /* preserve start error */ }
            throw;
        }

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            channel.Writer.TryComplete();
            try { await consumer.ConfigureAwait(false); } catch { /* preserve cancellation */ }
            throw;
        }

        channel.Writer.TryComplete();
        try { await consumer.ConfigureAwait(false); } catch { /* log delivery is best effort */ }

        return process.ExitCode;
    }
}

internal interface IWorkerProcessFactory
{
    IWorkerProcess Create(ProcessStartInfo startInfo);
}

internal interface IWorkerProcess : IDisposable
{
    event Action<string> OutputLine;
    event Action<string> ErrorLine;

    int ExitCode { get; }

    void Start();
    void BeginOutputReadLine();
    void BeginErrorReadLine();
    Task WaitForExitAsync(CancellationToken ct);
    void Kill(bool entireProcessTree);
}

internal sealed class SystemWorkerProcessFactory : IWorkerProcessFactory
{
    public IWorkerProcess Create(ProcessStartInfo startInfo) => new SystemWorkerProcess(startInfo);
}

internal sealed class SystemWorkerProcess : IWorkerProcess
{
    private readonly Process process;

    public SystemWorkerProcess(ProcessStartInfo startInfo)
    {
        process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                ErrorLine(e.Data);
        };
    }

    public event Action<string> OutputLine = delegate { };
    public event Action<string> ErrorLine = delegate { };

    public int ExitCode => process.ExitCode;

    public void Start() => process.Start();
    public void BeginOutputReadLine() => process.BeginOutputReadLine();
    public void BeginErrorReadLine() => process.BeginErrorReadLine();
    public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);
    public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
    public void Dispose() => process.Dispose();
}
