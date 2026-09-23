using System.Diagnostics;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class NuGetPackagePusher
{
    public static async Task<(bool Success, string? Error)> PushAsync(
        string workingDirectory,
        string packagePath,
        string apiKey,
        string source,
        Func<string, Task> onLine,
        CancellationToken ct = default)
    {
        return await PushAsync(workingDirectory, packagePath, apiKey, source, onLine, StreamingProcessRunner.Instance, ct);
    }

    public static async Task<(bool Success, string? Error)> PushAsync(
        string workingDirectory,
        string packagePath,
        string apiKey,
        string source,
        Func<string, Task> onLine,
        IStreamingProcessRunner processRunner,
        CancellationToken ct = default)
    {
        var psi = DeployerRunner.CreateDotnetProcessStartInfo(
            workingDirectory,
            ["nuget", "push", packagePath, "--api-key", apiKey, "--source", source, "--skip-duplicate"]);

        var exitCode = await processRunner.RunAsync(psi, async line =>
        {
            var sanitized = !string.IsNullOrEmpty(apiKey)
                ? line.Replace(apiKey, "***HIDDEN***", StringComparison.Ordinal)
                : line;
            await onLine(sanitized).ConfigureAwait(false);
        }, ct);

        if (exitCode != 0)
        {
            return (false, $"dotnet nuget push for '{Path.GetFileName(packagePath)}' exited with code {exitCode}");
        }

        return (true, null);
    }
}
