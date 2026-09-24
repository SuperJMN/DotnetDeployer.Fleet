namespace DotnetDeployer.Fleet.Core.Domain;

public enum NuGetReleasePackageState
{
    Prepared,
    Publishing,
    Incomplete,
    Complete,
    InterventionRequired,
    AwaitingIndex
}

/// <summary>Immutable release intent. Package bytes live in coordinator artifact storage.</summary>
public sealed record NuGetReleaseManifest(
    Guid ProjectId,
    string CommitSha,
    string Version,
    string Source,
    string ApiKeySecretName,
    bool RequiresGitHubPublish,
    IReadOnlyList<NuGetReleasePackage> Packages,
    DateTimeOffset PreparedAt);

public sealed record NuGetReleasePackage(
    string Id,
    string Version,
    string ArtifactPath,
    string Sha256,
    string ContentHash);

public sealed record NuGetReleasePackageProgress(
    string Id,
    NuGetReleasePackageState State,
    string? Detail,
    DateTimeOffset UpdatedAt);

public sealed record NuGetReleaseSnapshot(
    NuGetReleaseManifest Manifest,
    IReadOnlyList<NuGetReleasePackageProgress> Progress);
