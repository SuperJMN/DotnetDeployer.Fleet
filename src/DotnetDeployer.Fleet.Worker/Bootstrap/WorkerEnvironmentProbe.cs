using System.Diagnostics;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.WorkerService.Bootstrap;

/// <summary>
/// Detects the build environment this worker offers (Android SDK, JDK, .NET workloads, display,
/// free disk) so the coordinator can show from outside why a worker cannot build a project.
/// Listing workloads starts a process, so the snapshot is refreshed in the background at most
/// every <see cref="RefreshInterval"/> and heartbeats only read the latest one.
/// </summary>
public sealed class WorkerEnvironmentProbe(string repoStoragePath)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    private readonly object gate = new();
    private WorkerEnvironment? latest;
    private DateTimeOffset refreshedAt = DateTimeOffset.MinValue;
    private Task? refreshing;

    /// <summary>Latest snapshot (null until the first detection completes); triggers a refresh when stale.</summary>
    public WorkerEnvironment? Current()
    {
        lock (gate)
        {
            if (refreshing is null && DateTimeOffset.UtcNow - refreshedAt >= RefreshInterval)
                refreshing = Task.Run(RefreshAsync);
            return latest;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var detected = await DetectAsync(repoStoragePath, CancellationToken.None);
            lock (gate)
            {
                latest = detected;
                refreshedAt = DateTimeOffset.UtcNow;
            }
        }
        catch
        {
            // Best-effort: keep the previous snapshot and retry on the next interval.
            lock (gate) refreshedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            lock (gate) refreshing = null;
        }
    }

    public static async Task<WorkerEnvironment> DetectAsync(string repoStoragePath, CancellationToken ct)
    {
        return new WorkerEnvironment
        {
            AndroidSdkPath = ExistingDirectory("ANDROID_HOME") ?? ExistingDirectory("ANDROID_SDK_ROOT"),
            JavaHome = ExistingDirectory("JAVA_HOME"),
            Workloads = await ListWorkloadsAsync(ct),
            HasDisplay = HasDisplay(),
            FreeDiskMb = FreeDiskMb(repoStoragePath),
            ReportedAt = DateTimeOffset.UtcNow
        };
    }

    private static string? ExistingDirectory(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Directory.Exists(value) ? value : null;
    }

    private static bool HasDisplay()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            return true;

        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
               || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    private static long? FreeDiskMb(string path)
    {
        try
        {
            var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
            var existing = full;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
                existing = parent;

            var root = Path.GetPathRoot(existing) ?? existing;
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && existing.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault() ?? new DriveInfo(root);
            return drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>?> ListWorkloadsAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("workload");
            psi.ArgumentList.Add("list");
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            _ = await stderr;
            return process.ExitCode == 0 ? ParseWorkloadList(await stdout) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts workload ids from <c>dotnet workload list</c>: the first column of the table that
    /// follows the "Installed Workload Id" header, up to the trailing hint line.
    /// </summary>
    internal static IReadOnlyList<string> ParseWorkloadList(string output)
    {
        var workloads = new List<string>();
        var inTable = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Installed Workload Id", StringComparison.OrdinalIgnoreCase))
            {
                inTable = true;
                continue;
            }

            if (!inTable || line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("Use `dotnet workload search`", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Use 'dotnet workload search'", StringComparison.OrdinalIgnoreCase))
                break;

            var id = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (id.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_'))
                workloads.Add(id);
        }

        return workloads;
    }
}
