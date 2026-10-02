namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class SolutionTestRunner
{
    private const string OptOutLabel = "Run solution tests before deployment";

    internal static string DiscoverRootSolution(string repositoryRoot) =>
        SolutionDiscovery.DiscoverRootSolution(repositoryRoot);

    internal static Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars = null,
        CancellationToken ct = default)
    {
        return RunAsync(
            workingDirectory,
            onLine,
            envVars,
            null,
            StreamingProcessRunner.Instance,
            ct);
    }

    internal static Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars,
        IEnumerable<string>? scrubKeys,
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
        RootSolution solution;
        try
        {
            solution = SolutionDiscovery.Discover(workingDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }

        var solutionName = Path.GetFileName(solution.Path);
        await onLine($"Solution test target: {solutionName} ({solution.Reason})");

        try
        {
            var restore = DeployerRunner.CreateDotnetProcessStartInfo(
                workingDirectory,
                ["workload", "restore", solution.Path],
                envVars,
                scrubKeys);

            var restoreExitCode = await processRunner.RunAsync(restore, onLine, ct);
            if (restoreExitCode != 0)
            {
                await onLine(
                    $"[WARN] dotnet workload restore exited with code {restoreExitCode}; continuing to dotnet test.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await onLine($"[WARN] dotnet workload restore failed: {ex.Message}; continuing to dotnet test.");
        }

        try
        {
            var test = DeployerRunner.CreateDotnetProcessStartInfo(
                workingDirectory,
                ["test", solution.Path, "-c", "Release", "--nologo", "-m:1"],
                envVars,
                scrubKeys);

            var testExitCode = await processRunner.RunAsync(test, onLine, ct);
            return testExitCode == 0
                ? (true, null)
                : (false, $"dotnet test exited with code {testExitCode}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Could not run dotnet test: {ex.Message}");
        }
    }
}
