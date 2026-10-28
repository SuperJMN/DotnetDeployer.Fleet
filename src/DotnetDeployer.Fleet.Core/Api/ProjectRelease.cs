using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Core.Api;

/// <summary>
/// A NuGet release Fleet prepared for a project, with the job that published it (when the job
/// has not been cleared) and the state of each package.
/// </summary>
public sealed record ProjectRelease(
    string Version,
    string CommitSha,
    string Source,
    DateTimeOffset PreparedAt,
    Guid? JobId,
    JobStatus? JobStatus,
    NuGetVerificationStatus? Verification,
    IReadOnlyList<ProjectReleasePackage> Packages);

public sealed record ProjectReleasePackage(
    string Id,
    string Version,
    NuGetReleasePackageState State,
    string? Detail);
