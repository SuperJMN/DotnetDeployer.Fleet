namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class SolutionDiscovery
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
}
