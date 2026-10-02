namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal sealed record RootSolution(string Path, string Reason);

internal static class SolutionDiscovery
{
    private const string OptOutLabel = "Run solution tests before deployment";

    /// <summary>
    /// Picks the repository's root solution with the same preference as DotnetDeployer:
    /// a single <c>.slnx</c> wins (even next to a <c>.sln</c> during migration), otherwise a
    /// single <c>.sln</c>. Anything else is ambiguous and fails with every candidate listed.
    /// </summary>
    internal static RootSolution Discover(string repositoryRoot)
    {
        var candidates = Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => HasExtension(path, ".slnx") || HasExtension(path, ".sln"))
            .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var slnx = candidates.Where(path => HasExtension(path, ".slnx")).ToList();
        var sln = candidates.Where(path => HasExtension(path, ".sln")).ToList();

        if (slnx.Count == 1)
            return new RootSolution(slnx[0], sln.Count == 0
                ? "only solution in the repository root"
                : $".slnx preferred over {string.Join(", ", sln.Select(System.IO.Path.GetFileName))}");

        if (slnx.Count == 0 && sln.Count == 1)
            return new RootSolution(sln[0], "only solution in the repository root");

        var candidateList = candidates.Count == 0
            ? "(none)"
            : string.Join(", ", candidates.Select(System.IO.Path.GetFileName));
        var expectation = slnx.Count > 1
            ? "Expected at most one .slnx file"
            : "Expected exactly one .slnx or .sln file";

        throw new InvalidOperationException(
            $"{expectation} in repository root '{repositoryRoot}', " +
            $"but found {candidates.Count}. Candidates: {candidateList}. " +
            $"Keep a single .slnx (or a single .sln) in the repository root or disable '{OptOutLabel}' for this project.");
    }

    internal static string DiscoverRootSolution(string repositoryRoot) => Discover(repositoryRoot).Path;

    private static bool HasExtension(string path, string extension) =>
        System.IO.Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
}
