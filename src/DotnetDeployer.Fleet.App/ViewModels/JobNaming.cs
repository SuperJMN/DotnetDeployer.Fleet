using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Names jobs and commits the same way everywhere in the app. A job is named after its
/// detected version; until one is known it is named after its commit or, failing that,
/// after when it was queued, never after its internal id.
/// </summary>
internal static class JobNaming
{
    private const int ShortShaLength = 7;

    /// <summary>The abbreviated commit hash shown in lists, or null when the commit is unknown.</summary>
    public static string? ShortSha(string? sha) =>
        sha is { Length: > 0 } ? sha[..Math.Min(ShortShaLength, sha.Length)] : null;

    /// <summary>The name shown for the job in lists, such as "2.0.1" or "Commit 58f1c49".</summary>
    public static string Name(DeploymentJob job)
    {
        if (VersionDisplay.Visible(job.Version) is { Length: > 0 } version)
            return version;

        return ShortSha(job.TriggerCommitSha) is { } sha
            ? $"Commit {sha}"
            : $"Queued {QueuedAt(job)}";
    }

    /// <summary>The title of the job's own page, such as "Deployment 2.0.1" or "Deployment of commit 58f1c49".</summary>
    public static string Title(DeploymentJob job)
    {
        var kind = job.Kind == JobKind.PackageBuild ? "Package build" : "Deployment";

        if (VersionDisplay.Visible(job.Version) is { Length: > 0 } version)
            return $"{kind} {version}";

        return ShortSha(job.TriggerCommitSha) is { } sha
            ? $"{kind} of commit {sha}"
            : $"{kind} queued {QueuedAt(job)}";
    }

    // Retries move EnqueuedAt, so the first time the job was queued keeps its name stable.
    private static string QueuedAt(DeploymentJob job) =>
        LocalTimestampFormatter.Format(job.InitialEnqueuedAt ?? job.EnqueuedAt);
}
