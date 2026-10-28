using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Core.Api;

public enum PreflightSeverity
{
    Ok,
    Warning,
    Error
}

public sealed record PreflightCheck(string Name, PreflightSeverity Severity, string Message);

/// <summary>Whether a registered worker can build the project, and why not when it cannot.</summary>
public sealed record WorkerSuitability(
    Guid WorkerId,
    string Name,
    WorkerStatus Status,
    bool CanBuild,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Result of checking a project against Fleet's requirements without queueing anything:
/// root solution, deployer.yaml, referenced secrets, pinned packages and capable workers.
/// </summary>
public sealed record ProjectPreflightReport(
    Guid ProjectId,
    string ProjectName,
    IReadOnlyList<PreflightCheck> Checks,
    IReadOnlyList<string> ReferencedSecrets,
    IReadOnlyList<string> MissingSecrets,
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<WorkerSuitability> Workers)
{
    public bool Passed => Checks.All(check => check.Severity != PreflightSeverity.Error);
}
