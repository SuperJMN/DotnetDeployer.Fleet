namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class SolutionBuildRunner
{
    internal static Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars = null,
        IEnumerable<string>? scrubKeys = null,
        CancellationToken ct = default)
    {
        return RunAsync(
            workingDirectory,
            onLine,
            envVars,
            scrubKeys,
            StreamingProcessRunner.Instance,
            ct);
    }

    internal static Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars,
        IStreamingProcessRunner processRunner,
        CancellationToken ct = default)
    {
        return RunAsync(
            workingDirectory,
            onLine,
            envVars,
            null,
            processRunner,
            ct);
    }

    internal static async Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars,
        IEnumerable<string>? scrubKeys,
        IStreamingProcessRunner processRunner,
        CancellationToken ct = default)
    {
        string solution;
        try
        {
            solution = SolutionDiscovery.DiscoverRootSolution(workingDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }

        var solutionName = Path.GetFileName(solution);
        await onLine($"Solution build target: {solutionName}");

        try
        {
            var restore = DeployerRunner.CreateDotnetProcessStartInfo(
                workingDirectory,
                ["workload", "restore", solution],
                envVars,
                scrubKeys);

            var restoreExitCode = await processRunner.RunAsync(restore, onLine, ct);
            if (restoreExitCode != 0)
            {
                await onLine(
                    $"[WARN] dotnet workload restore exited with code {restoreExitCode}; continuing to dotnet build.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await onLine($"[WARN] dotnet workload restore failed: {ex.Message}; continuing to dotnet build.");
        }

        try
        {
            var build = DeployerRunner.CreateDotnetProcessStartInfo(
                workingDirectory,
                ["build", solution, "-c", "Release", "--nologo"],
                envVars,
                scrubKeys);

            var buildExitCode = await processRunner.RunAsync(build, onLine, ct);
            return buildExitCode == 0
                ? (true, null)
                : (false, $"dotnet build exited with code {buildExitCode}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Could not run dotnet build: {ex.Message}");
        }
    }
}
