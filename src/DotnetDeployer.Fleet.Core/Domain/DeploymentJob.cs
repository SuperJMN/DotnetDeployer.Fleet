namespace DotnetDeployer.Fleet.Core.Domain;

public class DeploymentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? WorkerId { get; set; }
    public JobKind Kind { get; set; } = JobKind.Deploy;
    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Commit SHA that triggered this job (null for manual deploys).</summary>
    public string? TriggerCommitSha { get; set; }

    public bool IsAutoTriggered { get; set; } = false;
    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Original queue time retained when a NuGet indexing wait requeues this job.</summary>
    public DateTimeOffset? InitialEnqueuedAt { get; set; }
    /// <summary>When the coordinator placed this job in a worker's queue (Status=Assigned).</summary>
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long? TotalDurationMs { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// JSON payload for <see cref="JobKind.PackageBuild"/> jobs. Stored on the
    /// job so queued work is self-contained even if project settings change later.
    /// </summary>
    public string? PackageRequestJson { get; set; }

    /// <summary>
    /// Coordinator's prediction of how long this job will take on the assigned worker, in
    /// milliseconds. Set when the job is pushed to a worker's queue. Informational — the
    /// scheduler uses it to estimate downstream queue depth.
    /// </summary>
    public long? EstimatedDurationMs { get; set; }

    /// <summary>Set when a user requests cancellation. Workers poll this to abort in-flight jobs.</summary>
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    /// <summary>
    /// Human-friendly version of the artifact being deployed (e.g. "1.2.3-beta.4").
    /// Detected on the fly by scanning incoming log lines for GitVersion / NBGV / MinVer
    /// output. Null until the first version line is observed; once set, it is not overwritten.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Desnormalized cache of the most recent open phase for this job (e.g.
    /// <c>worker.git.clone</c>, <c>package.generate.deb.x64</c>). Updated by the
    /// coordinator from worker phase events. Null when no phase is in flight.
    /// Persisted alongside <see cref="DeploymentJob"/> so the UI can render
    /// "current phase" without joining the JobPhases timeline.
    /// </summary>
    public string? CurrentPhase { get; set; }

    /// <summary>
    /// Timestamp of the most recent <c>phase.start</c> for this job. Used by the
    /// UI to compute "elapsed in current phase".
    /// </summary>
    public DateTimeOffset? CurrentPhaseStartedAt { get; set; }

    public void MarkFinished(DateTimeOffset finishedAt)
    {
        FinishedAt = finishedAt;
        TotalDurationMs = GetElapsedDurationMs(finishedAt);
        CurrentPhase = null;
        CurrentPhaseStartedAt = null;
    }

    public long? GetElapsedDurationMs(DateTimeOffset now)
    {
        if (FinishedAt is not null && TotalDurationMs is not null)
            return TotalDurationMs;

        var start = InitialEnqueuedAt ?? (EnqueuedAt != default ? EnqueuedAt : StartedAt ?? AssignedAt);
        if (start is null)
            return null;

        var end = FinishedAt ?? now;
        return Math.Max(0, (long)(end - start.Value).TotalMilliseconds);
    }
}
