using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DotnetDeployer.Fleet.Coordinator.Data;

/// <summary>
/// EF Core implementation of IFleetStorage. Uses IDbContextFactory so each
/// operation creates its own short-lived DbContext — safe for concurrent
/// singletons (BackgroundServices) and scoped API handlers alike.
/// </summary>
public class EfFleetStorage(IDbContextFactory<FleetDbContext> factory, IWorkerSelector selector) : IFleetStorage
{
    // Projects
    public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<Project?> GetProjectAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.FindAsync([id], ct);
    }

    public async Task AddProjectAsync(Project project, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateProjectAsync(Project project, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Projects.Update(project);
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordProjectPollAsync(Guid projectId, DateTimeOffset polledAt, string? commitSha, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = db.Projects.Where(p => p.Id == projectId);
        if (commitSha is null)
            await project.ExecuteUpdateAsync(set => set.SetProperty(p => p.LastPolledAt, polledAt), ct);
        else
            await project.ExecuteUpdateAsync(set => set
                .SetProperty(p => p.LastPolledAt, polledAt)
                .SetProperty(p => p.LastPolledCommitSha, commitSha), ct);
    }

    public async Task DeleteProjectAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var p = await db.Projects.FindAsync([id], ct);
        if (p != null)
        {
            db.Projects.Remove(p);
            await db.SaveChangesAsync(ct);
        }
    }

    // Jobs
    public async Task<IReadOnlyList<DeploymentJob>> GetJobsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs.OrderByDescending(j => j.EnqueuedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentJob>> GetJobsByProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs
            .Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.EnqueuedAt)
            .ToListAsync(ct);
    }

    public async Task<DeploymentJob?> GetJobAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs.FindAsync([id], ct);
    }

    public async Task AddJobAsync(DeploymentJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.DeploymentJobs.Add(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateJobAsync(DeploymentJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.DeploymentJobs.Update(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteFinishedJobsAsync(Guid? projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var terminal = new[] { JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled };

        // Resolve target job IDs first so log deletion uses the same set as the job
        // deletion. There is no FK cascade configured between LogEntry and DeploymentJob.
        var jobIdsQuery = db.DeploymentJobs.Where(j => terminal.Contains(j.Status));
        if (projectId is { } pid)
            jobIdsQuery = jobIdsQuery.Where(j => j.ProjectId == pid);

        var jobIds = await jobIdsQuery.Select(j => j.Id).ToListAsync(ct);
        if (jobIds.Count == 0) return 0;

        await db.LogEntries.Where(l => jobIds.Contains(l.JobId)).ExecuteDeleteAsync(ct);
        var deleted = await db.DeploymentJobs.Where(j => jobIds.Contains(j.Id)).ExecuteDeleteAsync(ct);
        return deleted;
    }

    public async Task<bool> SetJobVersionIfUnsetAsync(Guid jobId, string version, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Single UPDATE Jobs SET Version = @version WHERE Id = @jobId AND Version IS NULL.
        // Atomic at the SQL level: only the first concurrent caller's UPDATE matches a row
        // (rows == 1); every later caller sees Version IS NOT NULL and matches zero rows.
        var rows = await db.DeploymentJobs
            .Where(j => j.Id == jobId && j.Version == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.Version, version), ct);

        return rows > 0;
    }

    public async Task<IReadOnlyList<DeploymentJob>> GetUnassignedQueuedJobsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Queued && j.WorkerId == null)
            .OrderBy(j => j.EnqueuedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentJob>> GetActiveJobsForWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs
            .Where(j => j.WorkerId == workerId
                        && (j.Status == JobStatus.Assigned || j.Status == JobStatus.Running))
            .OrderBy(j => j.AssignedAt ?? j.EnqueuedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> AssignJobToWorkerAsync(Guid jobId, Guid workerId, long? estimatedDurationMs, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // Atomic guard: only flip Queued+unassigned → Assigned. Concurrent assigners
        // (or a leftover ClaimNextJobAsync caller) cannot overwrite each other because
        // the WHERE clause matches at most one row per call.
        var rows = await db.DeploymentJobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Queued && j.WorkerId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Assigned)
                .SetProperty(j => j.WorkerId, workerId)
                .SetProperty(j => j.AssignedAt, now)
                .SetProperty(j => j.EstimatedDurationMs, estimatedDurationMs), ct);

        return rows > 0;
    }

    public async Task<DeploymentJob?> GetNextAssignedJobForWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Assigned && j.WorkerId == workerId)
            .OrderBy(j => j.AssignedAt ?? j.EnqueuedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> TryStealAssignedJobAsync(Guid jobId, Guid newWorkerId, Guid expectedCurrentWorkerId, long? estimatedDurationMs, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // The Status==Assigned guard ensures we only steal queued (not in-flight) work,
        // and the WorkerId==expectedCurrentWorkerId guard prevents stealing from a worker
        // that already lost the job to someone else (or to the assigner reassigning it).
        var rows = await db.DeploymentJobs
            .Where(j => j.Id == jobId
                        && j.Status == JobStatus.Assigned
                        && j.WorkerId == expectedCurrentWorkerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.WorkerId, newWorkerId)
                .SetProperty(j => j.AssignedAt, now)
                .SetProperty(j => j.EstimatedDurationMs, estimatedDurationMs), ct);

        return rows > 0;
    }

    public async Task<IReadOnlyList<Guid>> UnassignJobsOfOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;

        // Targets: Assigned (queued on a worker) jobs whose worker is Offline AND has not
        // been seen for longer than the stale threshold. We do NOT touch Running jobs here
        // — those are owned by FailStaleJobsAsync (the worker actually died mid-run).
        var orphaned = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Assigned && j.WorkerId != null)
            .Join(
                db.Workers.Where(w => w.Status == WorkerStatus.Offline
                                      && w.LastSeenAt != null
                                      && w.LastSeenAt < cutoff),
                j => j.WorkerId, w => w.Id, (j, _) => j)
            .ToListAsync(ct);

        if (orphaned.Count == 0)
            return Array.Empty<Guid>();

        foreach (var job in orphaned)
        {
            job.Status = JobStatus.Queued;
            job.WorkerId = null;
            job.AssignedAt = null;
            job.EstimatedDurationMs = null;
        }

        await db.SaveChangesAsync(ct);
        return orphaned.Select(j => j.Id).ToList();
    }

    public async Task<JobDurationStat?> GetJobDurationStatAsync(Guid projectId, Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobDurationStats.FindAsync([projectId, workerId], ct);
    }

    public async Task<IReadOnlyList<JobDurationStat>> GetJobDurationStatsForProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobDurationStats.Where(s => s.ProjectId == projectId).ToListAsync(ct);
    }

    public async Task UpsertJobDurationStatAsync(Guid projectId, Guid workerId, double newEwmaMs, int samples, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.JobDurationStats.FindAsync([projectId, workerId], ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            db.JobDurationStats.Add(new JobDurationStat
            {
                ProjectId = projectId,
                WorkerId = workerId,
                EwmaMs = newEwmaMs,
                Samples = samples,
                LastUpdated = now,
            });
        }
        else
        {
            existing.EwmaMs = newEwmaMs;
            existing.Samples = samples;
            existing.LastUpdated = now;
        }
        await db.SaveChangesAsync(ct);
    }

    [Obsolete("Replaced by JobAssignmentService + GetNextAssignedJobForWorkerAsync.")]
    public async Task<DeploymentJob?> ClaimNextJobAsync(Guid workerId, CancellationToken ct = default)
    {
        // Backwards-compat shim for in-flight workers that still poll the legacy endpoint:
        // hand them whatever the new push scheduler has already assigned to them. Returns
        // null if nothing is queued for this worker — old workers will simply re-poll.
        var job = await GetNextAssignedJobForWorkerAsync(workerId, ct);
        if (job is not null)
            _ = selector; // selector kept as a dependency for backwards-compat tests.
        return job;
    }

    // Logs
    public async Task<IReadOnlyList<LogEntry>> GetLogsAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.LogEntries
            .Where(l => l.JobId == jobId)
            .OrderBy(l => l.Timestamp)
            .ToListAsync(ct);
    }

    public async Task AddLogEntriesAsync(IEnumerable<LogEntry> entries, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.LogEntries.AddRange(entries);
        await db.SaveChangesAsync(ct);
    }

    // Phases
    public async Task RecordJobPhaseAsync(Guid jobId, PhaseEvent ev, DateTimeOffset receivedAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        switch (ev.Kind)
        {
            case PhaseEventKind.Start:
            {
                var row = new JobPhase
                {
                    JobId = jobId,
                    Name = ev.Name,
                    StartedAt = receivedAt,
                    Status = PhaseStatus.Unknown,
                    AttrsJson = ev.Attrs.Count > 0
                        ? System.Text.Json.JsonSerializer.Serialize(ev.Attrs)
                        : null
                };
                db.JobPhases.Add(row);

                var job = await db.DeploymentJobs.FindAsync([jobId], ct);
                if (job is not null)
                {
                    job.CurrentPhase = ev.Name;
                    job.CurrentPhaseStartedAt = receivedAt;
                }
                break;
            }

            case PhaseEventKind.End:
            {
                // Match the most recent open row with the same name.
                var open = await db.JobPhases
                    .Where(p => p.JobId == jobId && p.Name == ev.Name && p.EndedAt == null)
                    .OrderByDescending(p => p.StartedAt)
                    .FirstOrDefaultAsync(ct);

                if (open is not null)
                {
                    open.EndedAt = receivedAt;
                    open.Status = ev.Status;
                    open.DurationMs = ev.DurationMs ?? (long)(receivedAt - open.StartedAt).TotalMilliseconds;
                    if (ev.Attrs.Count > 0)
                    {
                        // Merge end attrs (e.g. size_bytes) into the persisted row.
                        var existing = string.IsNullOrEmpty(open.AttrsJson)
                            ? new Dictionary<string, string>()
                            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(open.AttrsJson)
                                ?? new Dictionary<string, string>();
                        foreach (var kv in ev.Attrs) existing[kv.Key] = kv.Value;
                        open.AttrsJson = System.Text.Json.JsonSerializer.Serialize(existing);
                    }
                }
                else
                {
                    // No matching open row — record a synthetic closed entry so the timeline
                    // still reflects the event (e.g. lost start due to crash on the worker).
                    db.JobPhases.Add(new JobPhase
                    {
                        JobId = jobId,
                        Name = ev.Name,
                        StartedAt = receivedAt,
                        EndedAt = receivedAt,
                        Status = ev.Status,
                        DurationMs = ev.DurationMs,
                        AttrsJson = ev.Attrs.Count > 0
                            ? System.Text.Json.JsonSerializer.Serialize(ev.Attrs)
                            : null
                    });
                }

                // Update denormalized cache. If this end matches the current cache,
                // walk back to the most-recent still-open phase (LIFO stack semantics):
                // when an inner phase ends inside a wrapper (e.g. github.release.create
                // ending while github.deploy is still open), the UI should fall back to
                // showing the wrapper rather than blanking out.
                var job = await db.DeploymentJobs.FindAsync([jobId], ct);
                if (job is not null && job.CurrentPhase == ev.Name)
                {
                    // Persist the just-closed row before querying open phases again;
                    // otherwise SQLite still reports that row as open.
                    await db.SaveChangesAsync(ct);
                    var stillOpen = await db.JobPhases
                        .AsNoTracking()
                        .Where(p => p.JobId == jobId && p.EndedAt == null)
                        .OrderByDescending(p => p.StartedAt)
                        .Select(p => new { p.Name, p.StartedAt })
                        .FirstOrDefaultAsync(ct);
                    job.CurrentPhase = stillOpen?.Name;
                    job.CurrentPhaseStartedAt = stillOpen?.StartedAt;
                }
                break;
            }

            case PhaseEventKind.Info:
            {
                db.JobPhases.Add(new JobPhase
                {
                    JobId = jobId,
                    Name = ev.Name,
                    StartedAt = receivedAt,
                    EndedAt = receivedAt,
                    Status = PhaseStatus.Unknown,
                    Message = ev.Message,
                    AttrsJson = ev.Attrs.Count > 0
                        ? System.Text.Json.JsonSerializer.Serialize(ev.Attrs)
                        : null
                });
                break;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<JobPhase>> GetJobPhasesAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.JobPhases
            .Where(p => p.JobId == jobId)
            .OrderBy(p => p.StartedAt)
            .ToListAsync(ct);
    }

    // Workers
    public async Task<IReadOnlyList<Worker>> GetWorkersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Workers.OrderBy(w => w.Name).ToListAsync(ct);
    }

    public async Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Workers.FindAsync([id], ct);
    }

    public async Task AddWorkerAsync(Worker worker, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Workers.Add(worker);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Workers.Update(worker);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var worker = await db.Workers.FindAsync([workerId], ct);
        if (worker is null)
            return false;

        await db.RepoCaches.Where(r => r.WorkerId == workerId).ExecuteDeleteAsync(ct);
        await db.JobDurationStats.Where(s => s.WorkerId == workerId).ExecuteDeleteAsync(ct);

        db.Workers.Remove(worker);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TouchWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // ExecuteUpdate avoids the read+write round-trip and is safe to call
        // on the hot path of every authenticated worker request.
        var rows = await db.Workers
            .Where(w => w.Id == workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.LastSeenAt, now)
                .SetProperty(w => w.Status,
                    w => w.Status == WorkerStatus.Offline ? WorkerStatus.Online : w.Status),
                ct);

        return rows > 0;
    }

    // Stale detection
    public async Task<int> MarkOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;

        var staleWorkers = await db.Workers
            .Where(w => w.Status != WorkerStatus.Offline && w.LastSeenAt != null && w.LastSeenAt < cutoff)
            .ToListAsync(ct);

        foreach (var worker in staleWorkers)
            worker.Status = WorkerStatus.Offline;

        if (staleWorkers.Count > 0)
            await db.SaveChangesAsync(ct);

        return staleWorkers.Count;
    }

    public async Task<IReadOnlyList<Guid>> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;

        var staleJobs = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Running && j.WorkerId != null)
            .Join(db.Workers.Where(w => w.LastSeenAt != null && w.LastSeenAt < cutoff),
                  j => j.WorkerId, w => w.Id, (j, _) => j)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in staleJobs)
        {
            job.Status = JobStatus.Failed;
            job.MarkFinished(now);
            job.ErrorMessage = "Worker unresponsive — marked as failed by stale job reaper.";
        }

        if (staleJobs.Count > 0)
            await db.SaveChangesAsync(ct);

        return staleJobs.Select(j => j.Id).ToList();
    }

    public async Task<IReadOnlyList<Guid>> FailTimedOutJobsAsync(TimeSpan queuedTimeout, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - queuedTimeout;

        var timedOutJobs = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Queued && j.EnqueuedAt < cutoff)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in timedOutJobs)
        {
            job.Status = JobStatus.Failed;
            job.MarkFinished(now);
            job.ErrorMessage = "Timed out — no worker claimed this job within the allowed window.";
        }

        if (timedOutJobs.Count > 0)
            await db.SaveChangesAsync(ct);

        return timedOutJobs.Select(j => j.Id).ToList();
    }

    public Task<IReadOnlyList<Guid>> FailRunningJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default) =>
        FailJobsForWorker(workerId, reason, new[] { JobStatus.Running }, ct);

    public Task<IReadOnlyList<Guid>> FailJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default) =>
        FailJobsForWorker(workerId, reason, new[] { JobStatus.Running, JobStatus.Assigned }, ct);

    private async Task<IReadOnlyList<Guid>> FailJobsForWorker(
        Guid workerId,
        string reason,
        JobStatus[] statuses,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var liveJobs = await db.DeploymentJobs
            .Where(j => j.WorkerId == workerId
                        && statuses.Contains(j.Status))
            .ToListAsync(ct);

        if (liveJobs.Count == 0)
            return Array.Empty<Guid>();

        var now = DateTimeOffset.UtcNow;
        foreach (var job in liveJobs)
        {
            job.Status = JobStatus.Failed;
            job.MarkFinished(now);
            job.ErrorMessage = reason;
        }

        await db.SaveChangesAsync(ct);
        return liveJobs.Select(j => j.Id).ToList();
    }

    public async Task<IReadOnlyList<Guid>> FailStuckAssignedJobsAsync(TimeSpan assignedTimeout, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - assignedTimeout;

        // Jobs stuck in Assigned: claimed by a worker but never transitioned to Running.
        // EnqueuedAt is used as the baseline because there is no separate AssignedAt timestamp.
        var stuckJobs = await db.DeploymentJobs
            .Where(j => j.Status == JobStatus.Assigned && j.StartedAt == null && j.EnqueuedAt < cutoff)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in stuckJobs)
        {
            job.Status = JobStatus.Failed;
            job.MarkFinished(now);
            job.ErrorMessage = "Timed out — worker claimed this job but never started it.";
        }

        if (stuckJobs.Count > 0)
            await db.SaveChangesAsync(ct);

        return stuckJobs.Select(j => j.Id).ToList();
    }

    // Repo caches
    public async Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RepoCaches.Where(r => r.WorkerId == workerId).ToListAsync(ct);
    }

    public async Task<RepoCache?> GetRepoCacheAsync(Guid workerId, Guid projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RepoCaches.FirstOrDefaultAsync(r => r.WorkerId == workerId && r.ProjectId == projectId, ct);
    }

    public async Task UpsertRepoCacheAsync(RepoCache cache, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.RepoCaches.FirstOrDefaultAsync(
            r => r.WorkerId == cache.WorkerId && r.ProjectId == cache.ProjectId, ct);

        if (existing is null)
            db.RepoCaches.Add(cache);
        else
        {
            existing.LocalPath = cache.LocalPath;
            existing.SizeBytes = cache.SizeBytes;
            existing.LastUsedAt = cache.LastUsedAt;
            existing.LastKnownCommitSha = cache.LastKnownCommitSha;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRepoCacheAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.RepoCaches.FindAsync([id], ct);
        if (r != null)
        {
            db.RepoCaches.Remove(r);
            await db.SaveChangesAsync(ct);
        }
    }

    // Users
    public async Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.FirstOrDefaultAsync(u => u.Username == username.ToLower(), ct);
    }

    public async Task<User?> GetUserAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.OrderBy(u => u.Username).ToListAsync(ct);
    }

    public async Task AddUserAsync(User user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateUserAsync(User user, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var u = await db.Users.FindAsync([id], ct);
        if (u != null)
        {
            db.Users.Remove(u);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> AnyUserExistsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(ct);
    }

    // Secrets
    public async Task<IReadOnlyList<Secret>> GetSecretsAsync(Guid? projectId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Secrets
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<Secret?> GetSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Secrets.FindAsync([id], ct);
    }

    public async Task AddSecretAsync(Secret secret, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateSecretAsync(Secret secret, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Secrets.Update(secret);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSecretAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await db.Secrets.FindAsync([id], ct);
        if (s != null)
        {
            db.Secrets.Remove(s);
            await db.SaveChangesAsync(ct);
        }
    }
}
