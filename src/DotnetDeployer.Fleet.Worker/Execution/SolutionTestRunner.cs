namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class SolutionTestRunner
{
    private const string OptOutLabel = "Run solution tests before deployment";

    internal static string DiscoverRootSolution(string repositoryRoot)
    {
        var candidates = Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                       || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 1)
            return candidates[0];

        var candidateList = candidates.Count == 0
            ? "(none)"
            : string.Join(", ", candidates.Select(Path.GetFileName));

        throw new InvalidOperationException(
            $"Expected exactly one .slnx or .sln file in repository root '{repositoryRoot}', " +
            $"but found {candidates.Count}. Candidates: {candidateList}. " +
            $"Keep exactly one solution in the repository root or disable '{OptOutLabel}' for this project.");
    }

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
            StreamingProcessRunner.Instance,
            ct);
    }

    internal static async Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars,
        IStreamingProcessRunner processRunner,
        CancellationToken ct = default)
    {
        string solution;
        try
        {
            solution = DiscoverRootSolution(workingDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }

        var solutionName = Path.GetFileName(solution);
        await onLine($"Solution test target: {solutionName}");

        try
        {
            var restore = DeployerRunner.CreateDotnetProcessStartInfo(
                workingDirectory,
                ["workload", "restore", solution],
                envVars);

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
                ["test", solution, "-c", "Release", "--nologo"],
                envVars);

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
