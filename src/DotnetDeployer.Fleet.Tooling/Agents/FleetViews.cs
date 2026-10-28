using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tooling.Agents;

// Stable, documented shapes returned by the client commands (--json) and the MCP tools.
// Enums serialize as text. Secret values never appear in any of them.

public sealed record LastDeployView(
    Guid? JobId,
    JobStatus? Status,
    string? Version,
    string? CommitSha,
    NuGetVerificationStatus? NuGetVerification,
    DateTimeOffset? FinishedAt);

public sealed record JobBriefView(Guid Id, JobKind Kind, JobStatus Status, string? CommitSha, string? Version, DateTimeOffset EnqueuedAt);

public sealed record ProjectView(
    Guid Id,
    string Name,
    string GitUrl,
    string Branch,
    int PollingIntervalMinutes,
    bool RunTestsBeforeDeploy,
    IReadOnlyList<string> ExpectedPackageIds,
    LastDeployView? LastDeploy,
    JobBriefView? LatestJob);

public sealed record SecretView(string Name, string Scope, Guid? ProjectId, DateTimeOffset UpdatedAt);

public sealed record ProjectDetailView(
    ProjectView Project,
    bool HasGitToken,
    string? LastPolledCommitSha,
    DateTimeOffset? LastPolledAt,
    IReadOnlyList<SecretView> Secrets);

public sealed record EnsureProjectResult(string Outcome, IReadOnlyList<string> Changes, ProjectView Project);

public sealed record JobView(
    Guid Id,
    Guid ProjectId,
    string? ProjectName,
    JobKind Kind,
    JobStatus Status,
    Guid? WorkerId,
    string? WorkerName,
    string? CommitSha,
    bool IsAutoTriggered,
    string? Version,
    string? CurrentPhase,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long? DurationMs,
    NuGetVerificationStatus? NuGetVerification,
    string? ErrorMessage);

public sealed record JobDetailView(JobView Job, IReadOnlyList<ProjectReleasePackage> Packages);

public sealed record DeployResult(string Outcome, string CommitSha, JobView Job, IReadOnlyList<JobView> ExistingJobs)
{
    public const string Queued = "Queued";
    public const string AlreadyActive = "AlreadyActive";
    public const string AlreadyDeployed = "AlreadyDeployed";
}

public sealed record PhaseView(string Name, PhaseStatus Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, long? DurationMs, string? Message);

public sealed record WaitResult(JobView Job, string Until, bool TimedOut, int ExitCode);

public sealed record LogsResult(Guid JobId, JobStatus Status, int TotalLines, IReadOnlyList<JobLogLine> Lines);

public sealed record WorkerView(
    Guid Id,
    string Name,
    WorkerStatus Status,
    bool Disconnected,
    DateTimeOffset? LastSeenAt,
    string? Version,
    string? OperatingSystem,
    string? Architecture,
    int ProcessorCount,
    long TotalMemoryMb,
    Guid? CurrentJobId,
    string? CurrentJobProject,
    WorkerEnvironment? Environment);

public sealed record FeedComparison(
    string PackageId,
    string Source,
    IReadOnlyList<string> FleetVersions,
    IReadOnlyList<string> FeedVersions,
    IReadOnlyList<string> NotPublishedByFleet,
    string? Error);

public sealed record ReleasesResult(Guid ProjectId, string ProjectName, IReadOnlyList<ProjectRelease> Releases, IReadOnlyList<FeedComparison> Feed);

public sealed record SecretSetResult(string Outcome, SecretView Secret);

public sealed record StatusView(
    string? Url,
    bool Reachable,
    string? CoordinatorVersion,
    string? Username,
    string? Role,
    string CredentialSource,
    string? Error);

public sealed record LoginResult(string Url, string Username, string Role, string SettingsFile);

public sealed record DownloadResult(Guid JobId, string RelativePath, string OutputPath, long SizeBytes);
