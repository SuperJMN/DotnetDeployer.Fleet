using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Core.Interfaces;

public interface IFleetStorage
{
    // Projects
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default);
    Task<Project?> GetProjectAsync(Guid id, CancellationToken ct = default);
    Task AddProjectAsync(Project project, CancellationToken ct = default);
    Task UpdateProjectAsync(Project project, CancellationToken ct = default);

    /// <summary>
    /// Records a polling pass by writing only <see cref="Project.LastPolledAt"/> and, when
    /// given, <see cref="Project.LastPolledCommitSha"/>, so concurrent edits are preserved.
    /// </summary>
    Task RecordProjectPollAsync(Guid projectId, DateTimeOffset polledAt, string? commitSha, CancellationToken ct = default);
    Task DeleteProjectAsync(Guid id, CancellationToken ct = default);

    // Jobs
    Task<IReadOnlyList<DeploymentJob>> GetJobsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeploymentJob>> GetJobsByProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<DeploymentJob?> GetJobAsync(Guid id, CancellationToken ct = default);
    Task AddJobAsync(DeploymentJob job, CancellationToken ct = default);
    Task UpdateJobAsync(DeploymentJob job, CancellationToken ct = default);

    /// <summary>
    /// Deletes every job in a terminal state (<see cref="JobStatus.Succeeded"/>,
    /// <see cref="JobStatus.Failed"/> or <see cref="JobStatus.Cancelled"/>) along with
    /// their log entries. When <paramref name="projectId"/> is null, applies globally;
    /// otherwise only jobs for that project are removed. Returns the number of jobs deleted.
    /// </summary>
    Task<int> DeleteFinishedJobsAsync(Guid? projectId, CancellationToken ct = default);

    /// <summary>
    /// Atomically sets <see cref="DeploymentJob.Version"/> for the given job
    /// only if it is currently NULL. Returns true if the row was updated, false
    /// if the job did not exist or already had a version. Single SQL UPDATE
    /// with a WHERE Id = @id AND Version IS NULL guard — no app-level locking,
    /// no read-then-write race window.
    /// </summary>
    Task<bool> SetJobVersionIfUnsetAsync(Guid jobId, string version, CancellationToken ct = default);

    /// <summary>
    /// Returns jobs in <see cref="JobStatus.Queued"/> with no assigned worker, oldest first.
    /// Used by the JobAssignmentService to pick the next jobs to push into worker queues.
    /// </summary>
    Task<IReadOnlyList<DeploymentJob>> GetUnassignedQueuedJobsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns jobs in flight for a worker (Assigned + Running). Used to estimate the
    /// worker's pending workload when scheduling a new job.
    /// </summary>
    Task<IReadOnlyList<DeploymentJob>> GetActiveJobsForWorkerAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Atomically transitions a Queued, unassigned job to Assigned and pushes it to
    /// <paramref name="workerId"/>'s queue. Returns true if the assignment took effect.
    /// Returns false if the job was already claimed or no longer queued.
    /// </summary>
    Task<bool> AssignJobToWorkerAsync(Guid jobId, Guid workerId, long? estimatedDurationMs, CancellationToken ct = default);

    /// <summary>
    /// Returns the next <see cref="JobStatus.Assigned"/> job for <paramref name="workerId"/>,
    /// oldest assignment first. Does not transition state — the worker calls
    /// <c>ReportStarted</c> later to flip to Running.
    /// </summary>
    Task<DeploymentJob?> GetNextAssignedJobForWorkerAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Atomically reassigns an Assigned (not yet Running) job from its current worker to
    /// <paramref name="newWorkerId"/>. Returns true if the steal succeeded. Used for
    /// work-stealing: an idle worker pulls a job from a busy worker's queue when it can
    /// finish it sooner.
    /// </summary>
    Task<bool> TryStealAssignedJobAsync(Guid jobId, Guid newWorkerId, Guid expectedCurrentWorkerId, long? estimatedDurationMs, CancellationToken ct = default);

    /// <summary>
    /// Returns Assigned jobs whose worker has been offline for more than
    /// <paramref name="staleThreshold"/> back to <see cref="JobStatus.Queued"/> (clears
    /// WorkerId and AssignedAt). Returns the IDs so the caller can notify the assigner.
    /// </summary>
    Task<IReadOnlyList<Guid>> UnassignJobsOfOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default);

    /// <summary>
    /// Reads the EWMA duration estimate for the (project, worker) pair, or null if no
    /// samples have been recorded yet.
    /// </summary>
    Task<JobDurationStat?> GetJobDurationStatAsync(Guid projectId, Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// All EWMA stats for a project across workers. Used as a fallback when the (project,
    /// worker) pair has no samples but other workers have run the project.
    /// </summary>
    Task<IReadOnlyList<JobDurationStat>> GetJobDurationStatsForProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Updates the EWMA for the given (project, worker) with a new observed duration.
    /// </summary>
    Task UpsertJobDurationStatAsync(Guid projectId, Guid workerId, double newEwmaMs, int samples, CancellationToken ct = default);

    /// <summary>
    /// [Obsolete] Pull-based claim used by older worker builds. Prefer
    /// <see cref="GetNextAssignedJobForWorkerAsync"/> in conjunction with the push-based
    /// <c>JobAssignmentService</c>. Kept temporarily so any in-flight worker can still
    /// drain itself during a rolling upgrade.
    /// </summary>
    [Obsolete("Replaced by the push-based scheduler. Use GetNextAssignedJobForWorkerAsync.")]
    Task<DeploymentJob?> ClaimNextJobAsync(Guid workerId, CancellationToken ct = default);

    // Logs
    Task<IReadOnlyList<LogEntry>> GetLogsAsync(Guid jobId, CancellationToken ct = default);
    Task AddLogEntriesAsync(IEnumerable<LogEntry> entries, CancellationToken ct = default);

    // Phases
    /// <summary>
    /// Records a phase event (start/end/info) for a job. For <c>start</c> events,
    /// inserts a new row. For <c>end</c> events, updates the most recent matching
    /// open row (same JobId+Name). For <c>info</c> events, inserts a self-contained
    /// row with EndedAt == StartedAt. Also updates the desnormalized
    /// <c>Job.CurrentPhase</c> / <c>Job.CurrentPhaseStartedAt</c> caches so
    /// <c>GetJobAsync</c> can render "current phase" without a join.
    /// </summary>
    Task RecordJobPhaseAsync(Guid jobId, PhaseEvent ev, DateTimeOffset receivedAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the chronological phase timeline for a job (oldest first).
    /// </summary>
    Task<IReadOnlyList<JobPhase>> GetJobPhasesAsync(Guid jobId, CancellationToken ct = default);

    // Workers
    Task<IReadOnlyList<Worker>> GetWorkersAsync(CancellationToken ct = default);
    Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default);
    Task AddWorkerAsync(Worker worker, CancellationToken ct = default);
    Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default);
    Task<bool> DeleteWorkerAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>
    /// Cheap liveness ping: updates <c>LastSeenAt = UtcNow</c> for the worker
    /// and, if it was <see cref="WorkerStatus.Offline"/>, flips it back to
    /// <see cref="WorkerStatus.Online"/>. Returns <c>true</c> if the worker exists.
    /// Designed to be called from any authenticated worker endpoint so that
    /// heartbeats are not the single point of failure for liveness detection.
    /// </summary>
    Task<bool> TouchWorkerAsync(Guid workerId, CancellationToken ct = default);

    // Repo caches
    Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default);
    Task<RepoCache?> GetRepoCacheAsync(Guid workerId, Guid projectId, CancellationToken ct = default);
    Task UpsertRepoCacheAsync(RepoCache cache, CancellationToken ct = default);
    Task DeleteRepoCacheAsync(Guid id, CancellationToken ct = default);

    // Users
    Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken ct = default);
    Task AddUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<bool> AnyUserExistsAsync(CancellationToken ct = default);

    // Stale detection
    /// <summary>
    /// Marks workers whose <c>LastSeenAt</c> is older than <paramref name="staleThreshold"/> as <see cref="WorkerStatus.Offline"/>.
    /// Returns the number of workers affected.
    /// </summary>
    Task<int> MarkOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default);

    /// <summary>
    /// Fails jobs that are still <see cref="JobStatus.Running"/> but whose worker has not
    /// heartbeated within <paramref name="staleThreshold"/>. Assigned jobs have not started
    /// yet and should be reclaimed through <see cref="UnassignJobsOfOfflineWorkersAsync"/>.
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default);

    /// <summary>
    /// Fails jobs that have been <see cref="JobStatus.Queued"/> longer than <paramref name="queuedTimeout"/>
    /// without being claimed by a worker.
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailTimedOutJobsAsync(TimeSpan queuedTimeout, CancellationToken ct = default);

    /// <summary>
    /// Fails jobs that have been <see cref="JobStatus.Assigned"/> longer than <paramref name="assignedTimeout"/>
    /// without transitioning to <see cref="JobStatus.Running"/>. This catches jobs that were claimed
    /// by a worker but never started (e.g. due to a crash between claim and execution).
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailStuckAssignedJobsAsync(TimeSpan assignedTimeout, CancellationToken ct = default);

    /// <summary>
    /// Fails every <see cref="JobStatus.Running"/> job owned by <paramref name="workerId"/>.
    /// Used when a worker explicitly announces that it is no longer running anything:
    /// assigned jobs are still queued work and must remain claimable.
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailRunningJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Fails every non-terminal job (<see cref="JobStatus.Assigned"/> or <see cref="JobStatus.Running"/>)
    /// owned by <paramref name="workerId"/>. Used for explicit administrative cleanup of a worker's
    /// whole live queue.
    /// Returns the IDs of the jobs that were marked as failed.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default);

    // Secrets
    /// <summary>Returns global secrets when <paramref name="projectId"/> is null, or project-scoped secrets otherwise.</summary>
    Task<IReadOnlyList<Secret>> GetSecretsAsync(Guid? projectId, CancellationToken ct = default);
    Task<Secret?> GetSecretAsync(Guid id, CancellationToken ct = default);
    Task AddSecretAsync(Secret secret, CancellationToken ct = default);
    Task UpdateSecretAsync(Secret secret, CancellationToken ct = default);
    Task DeleteSecretAsync(Guid id, CancellationToken ct = default);
}
