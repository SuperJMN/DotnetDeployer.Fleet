using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Channels;

namespace DotnetDeployer.Fleet.Coordinator.Endpoints;

public static class JobEndpoints
{
    private const long MaxArtifactUploadBytes = 512L * 1024 * 1024;
    private const long MaxNuGetPackageUploadBytes = 64L * 1024 * 1024;

    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs").RequireAuthorization();

        group.MapGet("/", GetAll);
        group.MapGet("/{id:guid}", GetById);
        group.MapGet("/{id:guid}/logs", StreamLogs);
        group.MapGet("/{id:guid}/phases", GetPhases);
        group.MapGet("/{id:guid}/artifacts", GetArtifacts);
        group.MapGet("/{id:guid}/artifacts/{*relativePath}", DownloadArtifact);
        group.MapGet("/{id:guid}/nuget-release", GetNuGetReleaseForAdmin).RequireAuthorization("Admin");
        group.MapDelete("/finished", DeleteFinishedJobs);
        group.MapPost("/{id:guid}/cancel", CancelJob);

        // Worker internal endpoints (authenticated by worker secret header)
        var workerGroup = app.MapGroup("/api/queue").AddEndpointFilter<WorkerLivenessFilter>();
        workerGroup.MapGet("/next", GetNextJob).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/start", ReportStarted).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/logs", AppendLogs).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/phase", AppendPhase).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/artifacts", UploadArtifact)
            .WithMetadata(new RequestSizeLimitAttribute(MaxArtifactUploadBytes))
            .RequireAuthorization("Worker");
        workerGroup.MapGet("/jobs/{id:guid}/nuget-release/{commitSha}", GetNuGetRelease).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/nuget-release/{commitSha}/packages/{packageId}", UploadNuGetReleasePackage)
            .WithMetadata(new RequestSizeLimitAttribute(MaxNuGetPackageUploadBytes))
            .RequireAuthorization("Worker");
        workerGroup.MapGet("/jobs/{id:guid}/nuget-release/{commitSha}/packages/{packageId}", DownloadNuGetReleasePackage).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/nuget-release/{commitSha}", CreateNuGetRelease).RequireAuthorization("Worker");
        workerGroup.MapPut("/jobs/{id:guid}/nuget-release/{commitSha}/packages/{packageId}/state", SetNuGetReleasePackageState).RequireAuthorization("Worker");
        workerGroup.MapPost("/jobs/{id:guid}/complete", ReportCompleted).RequireAuthorization("Worker");
        workerGroup.MapGet("/jobs/{id:guid}/should-cancel", ShouldCancel).RequireAuthorization("Worker");
    }

    private static async Task<IResult> GetAll(IFleetStorage storage)
    {
        var jobs = await storage.GetJobsAsync();
        return Results.Ok(jobs);
    }

    private static async Task<IResult> GetById(Guid id, IFleetStorage storage)
    {
        var job = await storage.GetJobAsync(id);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    private static async Task<IResult> GetNuGetReleaseForAdmin(Guid id, IFleetStorage storage, NuGetReleaseStore releases)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(job.TriggerCommitSha)) return Results.NotFound();
        try { return (await releases.GetAsync(job.ProjectId, job.TriggerCommitSha)) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound(); }
        catch (ArgumentException) { return Results.NotFound(); }
    }

    private static async Task<IResult> DeleteFinishedJobs(
        IFleetStorage storage,
        PackageArtifactStore artifacts)
    {
        var terminal = new[] { JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled };
        var artifactJobs = (await storage.GetJobsAsync())
            .Where(job => job.Kind == JobKind.PackageBuild && terminal.Contains(job.Status))
            .ToList();

        var deleted = await storage.DeleteFinishedJobsAsync(null);

        foreach (var job in artifactJobs)
            await artifacts.DeleteAsync(job);

        return Results.Ok(new { deleted });
    }

    /// <summary>
    /// Server-Sent Events stream of log lines for a given job.
    /// Existing logs are sent first, then new lines are pushed as they arrive.
    /// For completed jobs the stream closes immediately after existing logs.
    /// While the job is waiting to be picked up, periodic status events are sent
    /// so the client can show feedback (e.g. "Waiting for an available worker…").
    /// </summary>
    private static async Task StreamLogs(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage,
        LogBroadcaster broadcaster,
        CancellationToken ct)
    {
        httpContext.Response.Headers["Content-Type"] = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"] = "no-cache";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        await httpContext.Response.Body.FlushAsync(ct);

        // Subscribe BEFORE reading existing logs so no messages are lost
        // in the window between the DB read and the subscription.
        var channel = broadcaster.Subscribe(id);
        var seenLogIds = new HashSet<Guid>();
        try
        {
            // Send existing logs first
            var existing = await storage.GetLogsAsync(id, ct);
            foreach (var entry in existing)
            {
                seenLogIds.Add(entry.Id);
                await WriteEventAsync(httpContext.Response, entry.Line, ct);
            }

            // If job is already in a terminal state, close the stream
            var job = await storage.GetJobAsync(id, ct);
            bool isTerminal = job is null
                || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

            if (isTerminal)
            {
                await WriteStatusEventAsync(httpContext.Response, job?.Status ?? JobStatus.Failed, ct);
                return;
            }

            // Send initial status so the client shows feedback immediately
            var lastKnownStatus = job!.Status;
            await WriteStatusEventAsync(httpContext.Response, lastKnownStatus, ct);

            if (lastKnownStatus == JobStatus.Queued)
                await WriteEventAsync(httpContext.Response, "⏳ Waiting for an available worker…", ct);

            // Stream new log lines as they arrive, polling job status on each heartbeat
            // so the client gets timely feedback during the Queued→Running transition.
            var statusPollInterval = TimeSpan.FromSeconds(5);
            // Convert ValueTask to Task exactly once per ReadAsync call.
            // ValueTask must never be awaited / AsTask'd more than once.
            var pendingRead = channel.Reader.ReadAsync(ct).AsTask();
            while (!ct.IsCancellationRequested)
            {
                var delayTask = Task.Delay(statusPollInterval, ct);
                var winner = await Task.WhenAny(pendingRead, delayTask);

                if (winner == pendingRead)
                {
                    var entry = await pendingRead;
                    if (seenLogIds.Add(entry.Id))
                        await WriteEventAsync(httpContext.Response, entry.Line, ct);
                    pendingRead = channel.Reader.ReadAsync(ct).AsTask();
                }
                else
                {
                    // Poll job status — send an update when it changes, otherwise keep-alive
                    var current = await storage.GetJobAsync(id, ct);

                    if (current is null
                        || current.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
                    {
                        await WriteStatusEventAsync(httpContext.Response, current?.Status ?? JobStatus.Failed, ct);
                        break;
                    }

                    if (current.Status != lastKnownStatus)
                    {
                        lastKnownStatus = current.Status;
                        await WriteStatusEventAsync(httpContext.Response, lastKnownStatus, ct);

                        var transitionMsg = lastKnownStatus switch
                        {
                            JobStatus.Assigned => "🔄 Worker assigned, preparing deployment…",
                            JobStatus.Running => "🚀 Deployment started",
                            _ => null
                        };
                        if (transitionMsg is not null)
                            await WriteEventAsync(httpContext.Response, transitionMsg, ct);
                    }
                    else
                    {
                        await httpContext.Response.WriteAsync(": keep-alive\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (ChannelClosedException) { /* job finished */ }
        finally
        {
            broadcaster.Unsubscribe(id, channel);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string data, CancellationToken ct)
    {
        await response.WriteAsync($"data: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteStatusEventAsync(HttpResponse response, JobStatus status, CancellationToken ct)
    {
        await response.WriteAsync($"event: status\ndata: {status}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    // Worker-facing endpoints

    private static async Task<IResult> GetNextJob(
        HttpContext httpContext,
        IFleetStorage storage,
        IDurationEstimator estimator,
        LogBroadcaster broadcaster)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        // Push model: the coordinator has already assigned jobs to this worker. Hand
        // back the oldest one. Workers running the legacy ClaimNextJobAsync code path
        // will still get fed because EfFleetStorage.ClaimNextJobAsync now delegates to
        // the same query.
        var assigned = await storage.GetNextAssignedJobForWorkerAsync(workerId);
        if (assigned is not null)
            return Results.Ok(assigned);

        // Caller's queue is empty. Try to steal a queued (Assigned, not yet Running) job
        // from a busy worker if we can finish it materially sooner. The threshold avoids
        // ping-pong between workers with similar specs.
        var caller = await storage.GetWorkerAsync(workerId);
        if (caller is null)
            return Results.NoContent();

        var allWorkers = await storage.GetWorkersAsync();
        var workersById = allWorkers.ToDictionary(w => w.Id);

        // Build a queue per worker so we can compute ETA-of-this-job-on-its-current-owner.
        var jobsByWorker = new Dictionary<Guid, List<DeploymentJob>>();
        foreach (var w in allWorkers)
        {
            var jobs = await storage.GetActiveJobsForWorkerAsync(w.Id);
            jobsByWorker[w.Id] = jobs.ToList();
        }

        const double stealRatio = 1.2; // require ≥20% improvement to steal.
        DeploymentJob? stolen = null;

        // Walk other workers and look at the *tail* of each queue first (cheapest to steal).
        foreach (var (otherId, otherJobs) in jobsByWorker)
        {
            if (otherId == workerId) continue;

            for (var i = otherJobs.Count - 1; i >= 0; i--)
            {
                var candidate = otherJobs[i];
                if (candidate.Status != JobStatus.Assigned) continue;

                if (!JobAssignmentService.IsCompatible(candidate, caller)) continue;

                // ETA for the candidate as it sits today on its current owner = sum of
                // remaining time of the jobs ahead of it (indexes 0..i-1 in this sorted
                // list, since AssignedAt asc). Then add the candidate's own estimate.
                var aheadOfCandidate = otherJobs.Take(i).ToList();
                var currentEta = JobAssignmentService.ComputeWorkerLoadMs(aheadOfCandidate)
                                 + (candidate.EstimatedDurationMs ?? EwmaDurationEstimator.DefaultMs);

                var newEstimate = await estimator.EstimateAsync(candidate, caller);
                var callerEta = newEstimate; // caller's own queue is empty (we checked above).

                if (currentEta > callerEta * stealRatio)
                {
                    var ok = await storage.TryStealAssignedJobAsync(candidate.Id, workerId, otherId, newEstimate);
                    if (ok)
                    {
                        stolen = await storage.GetJobAsync(candidate.Id);
                        var line = $"[scheduler] Stolen by {(string.IsNullOrWhiteSpace(caller.Name) ? caller.Id.ToString("N")[..8] : caller.Name)} " +
                                   $"(ETA improved from ~{currentEta / 1000.0:0.#}s to ~{callerEta / 1000.0:0.#}s).";
                        var entry = new LogEntry { JobId = candidate.Id, Line = line, Timestamp = DateTimeOffset.UtcNow };
                        await storage.AddLogEntriesAsync([entry]);
                        broadcaster.Publish(candidate.Id, entry);
                        break;
                    }
                }
            }

            if (stolen is not null) break;
        }

        return stolen is not null ? Results.Ok(stolen) : Results.NoContent();
    }

    private static async Task<IResult> ReportStarted(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();

        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            return Results.Conflict(new { message = "Job is already in a terminal state." });

        if (job.Status != JobStatus.Assigned)
            return Results.Conflict(new { message = "Job is not assigned." });

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await storage.UpdateJobAsync(job);
        return Results.Ok();
    }

    private static async Task<IResult> AppendLogs(
        Guid id,
        [FromBody] AppendLogsRequest req,
        HttpContext httpContext,
        IFleetStorage storage,
        LogBroadcaster broadcaster,
        ILoggerFactory loggerFactory)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();

        var entries = req.Lines.Select(line => new LogEntry
        {
            JobId = id,
            Line = line,
            Timestamp = DateTimeOffset.UtcNow
        }).ToList();

        await storage.AddLogEntriesAsync(entries);

        foreach (var entry in entries)
            broadcaster.Publish(id, entry);

        // Version detection is best-effort and must NEVER fail the log-append:
        // logs are already persisted (line above) and broadcast. If the tracker
        // throws (e.g. transient DB lock, malformed line, etc.) we log a warning
        // and return 200 so the worker doesn't retry and duplicate the batch.
        try
        {
            await DeploymentVersionTracker.TryUpdateVersionAsync(storage, job, req.Lines);
        }
        catch (Exception ex)
        {
            loggerFactory
                .CreateLogger(typeof(JobEndpoints).FullName!)
                .LogWarning(ex, "Deployment version detection failed for job {JobId}; logs were persisted regardless.", id);
        }

        return Results.Ok();
    }

    private static async Task<IResult> ReportCompleted(
        Guid id,
        [FromBody] CompleteJobRequest req,
        HttpContext httpContext,
        IFleetStorage storage,
        NuGetReleaseStore releases,
        LogBroadcaster broadcaster,
        JobAssignmentSignal signal)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();

        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            return Results.Conflict(new { message = "Job is already in a terminal state." });

        var now = DateTimeOffset.UtcNow;
        if (req.AwaitingNuGetIndex && job.CancellationRequestedAt is null)
        {
            if (req.Success || job.Kind != JobKind.Deploy || string.IsNullOrWhiteSpace(job.TriggerCommitSha))
                return Results.BadRequest(new { message = "Only an incomplete NuGet release can await indexing." });
            var release = await releases.GetAsync(job.ProjectId, job.TriggerCommitSha);
            if (release is null || !release.Progress.Any(p => p.State == NuGetReleasePackageState.AwaitingIndex)
                || release.Progress.Any(p => p.State == NuGetReleasePackageState.InterventionRequired))
                return Results.BadRequest(new { message = "The release is not awaiting NuGet indexing." });

            job.Status = JobStatus.AwaitingNuGetIndex;
            job.WorkerId = null;
            job.AssignedAt = null;
            job.StartedAt = null;
            job.EstimatedDurationMs = null;
            job.CurrentPhase = null;
            job.CurrentPhaseStartedAt = null;
            job.ErrorMessage = null;
        }
        else
        {
            job.Status = job.CancellationRequestedAt is not null && !req.Success
                ? JobStatus.Cancelled
                : req.Success ? JobStatus.Succeeded : JobStatus.Failed;
            job.MarkFinished(now);
            job.ErrorMessage = req.ErrorMessage;
        }
        await storage.UpdateJobAsync(job);

        // EWMA update: only on success — failed runs are noise, not signal. We need a
        // measured StartedAt to compute duration; a worker that never reported start
        // (legacy or buggy) just doesn't contribute to the model.
        if (job.Status == JobStatus.Succeeded && job.StartedAt is { } startedAt)
        {
            var observedMs = Math.Max(1, (long)(now - startedAt).TotalMilliseconds);
            var existing = await storage.GetJobDurationStatAsync(job.ProjectId, workerId);
            const double alpha = 0.3;
            var newEwma = existing is null ? observedMs : alpha * observedMs + (1 - alpha) * existing.EwmaMs;
            var samples = (existing?.Samples ?? 0) + 1;
            await storage.UpsertJobDurationStatAsync(job.ProjectId, workerId, newEwma, samples);
        }

        if (job.Status != JobStatus.AwaitingNuGetIndex)
            broadcaster.Complete(id);

        // The worker just freed a slot — wake the scheduler so the next queued job (if
        // any) gets considered for this worker straight away.
        signal.Notify();
        return Results.Ok();
    }

    public record AppendLogsRequest(string[] Lines);
    public record CompleteJobRequest(bool Success, string? ErrorMessage, bool AwaitingNuGetIndex = false);

    /// <summary>
    /// Wire-format for a phase event posted by a worker. Mirrors
    /// <see cref="PhaseEvent"/> but uses string Kind/Status to keep the wire
    /// stable across enum reorderings.
    /// </summary>
    public record AppendPhaseRequest(
        string Kind,
        string Name,
        string? Status,
        long? DurationMs,
        string? Message,
        Dictionary<string, string>? Attrs);

    private static async Task<IResult> GetPhases(Guid id, IFleetStorage storage)
    {
        var phases = await storage.GetJobPhasesAsync(id);
        return Results.Ok(phases);
    }

    private static async Task<IResult> AppendPhase(
        Guid id,
        [FromBody] AppendPhaseRequest req,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();

        if (string.IsNullOrEmpty(req.Name))
            return Results.BadRequest(new { error = "name is required" });

        if (!Enum.TryParse<PhaseEventKind>(req.Kind, ignoreCase: true, out var kind))
            return Results.BadRequest(new { error = $"unknown kind '{req.Kind}'" });

        var status = PhaseStatus.Unknown;
        if (!string.IsNullOrEmpty(req.Status))
            Enum.TryParse(req.Status, ignoreCase: true, out status);

        var ev = new PhaseEvent
        {
            Kind = kind,
            Name = req.Name,
            Status = status,
            DurationMs = req.DurationMs,
            Message = req.Message,
            Attrs = req.Attrs ?? new Dictionary<string, string>()
        };

        await storage.RecordJobPhaseAsync(id, ev, DateTimeOffset.UtcNow);
        return Results.Ok();
    }

    private static async Task<IResult> UploadArtifact(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage,
        PackageArtifactStore artifacts)
    {
        if (!TryGetWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();
        if (job.WorkerId != workerId) return Results.Forbid();
        if (job.Kind != JobKind.PackageBuild)
            return Results.BadRequest(new { error = "Artifacts can only be uploaded for package build jobs." });

        var relativePath = httpContext.Request.Headers["X-Fleet-Artifact-Path"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(relativePath))
            return Results.BadRequest(new { error = "X-Fleet-Artifact-Path header is required." });

        try
        {
            var saved = await artifacts.SaveAsync(job, relativePath, httpContext.Request.Body, httpContext.RequestAborted);
            return Results.Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<DeploymentJob?> OwnedReleaseJob(Guid id, HttpContext context, IFleetStorage storage)
    {
        if (!TryGetWorkerId(context, out var workerId)) return null;
        var job = await storage.GetJobAsync(id);
        return job is { Kind: JobKind.Deploy, Status: JobStatus.Running } && job.WorkerId == workerId ? job : null;
    }

    private static async Task<IResult> GetNuGetRelease(Guid id, string commitSha, HttpContext context,
        IFleetStorage storage, NuGetReleaseStore releases)
    {
        var job = await OwnedReleaseJob(id, context, storage);
        if (job is null) return Results.Forbid();
        if (!string.Equals(job.TriggerCommitSha, commitSha, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        try { return (await releases.GetAsync(job.ProjectId, commitSha)) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> UploadNuGetReleasePackage(Guid id, string commitSha, string packageId,
        HttpContext context, IFleetStorage storage, NuGetReleaseStore releases)
    {
        var job = await OwnedReleaseJob(id, context, storage);
        if (job is null) return Results.Forbid();
        if (!string.Equals(job.TriggerCommitSha, commitSha, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        try
        {
            await releases.UploadPackageAsync(job.ProjectId, commitSha, packageId, context.Request.Body, context.RequestAborted);
            return Results.Ok();
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private static async Task<IResult> DownloadNuGetReleasePackage(Guid id, string commitSha, string packageId,
        HttpContext context, IFleetStorage storage, NuGetReleaseStore releases)
    {
        var job = await OwnedReleaseJob(id, context, storage);
        if (job is null) return Results.Forbid();
        if (!string.Equals(job.TriggerCommitSha, commitSha, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        try { return Results.File(releases.OpenPackage(job.ProjectId, commitSha, packageId), "application/octet-stream"); }
        catch (FileNotFoundException) { return Results.NotFound(); }
        catch (DirectoryNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> CreateNuGetRelease(Guid id, string commitSha, NuGetReleaseManifest manifest,
        HttpContext context, IFleetStorage storage, NuGetReleaseStore releases)
    {
        var job = await OwnedReleaseJob(id, context, storage);
        if (job is null) return Results.Forbid();
        if (!string.Equals(job.TriggerCommitSha, commitSha, StringComparison.OrdinalIgnoreCase)
            || manifest.ProjectId != job.ProjectId || !string.Equals(manifest.CommitSha, commitSha, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest();
        try { return Results.Ok(await releases.CreateAsync(manifest, context.RequestAborted)); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private sealed record ReleaseStateRequest(NuGetReleasePackageState State, string? Detail);

    private static async Task<IResult> SetNuGetReleasePackageState(Guid id, string commitSha, string packageId,
        ReleaseStateRequest request, HttpContext context, IFleetStorage storage, NuGetReleaseStore releases)
    {
        var job = await OwnedReleaseJob(id, context, storage);
        if (job is null) return Results.Forbid();
        if (!string.Equals(job.TriggerCommitSha, commitSha, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
        try { return Results.Ok(await releases.SetStateAsync(job.ProjectId, commitSha, packageId, request.State, request.Detail, context.RequestAborted)); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private static async Task<IResult> GetArtifacts(
        Guid id,
        IFleetStorage storage,
        PackageArtifactStore artifacts)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();

        var result = await artifacts.ListAsync(job);
        return Results.Ok(result);
    }

    private static async Task<IResult> DownloadArtifact(
        Guid id,
        string relativePath,
        IFleetStorage storage,
        PackageArtifactStore artifacts)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();

        try
        {
            var stream = artifacts.OpenRead(job, relativePath);
            return Results.File(stream, "application/octet-stream", artifacts.GetFileName(relativePath));
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> CancelJob(
        Guid id,
        IFleetStorage storage,
        LogBroadcaster broadcaster,
        JobAssignmentSignal signal)
    {
        var job = await storage.GetJobAsync(id);
        if (job is null) return Results.NotFound();

        if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            return Results.Conflict(new { message = "Job is already in a terminal state." });

        var now = DateTimeOffset.UtcNow;
        job.CancellationRequestedAt = now;
        var completedImmediately = job.Status is JobStatus.Queued or JobStatus.Assigned or JobStatus.AwaitingNuGetIndex;

        if (completedImmediately)
        {
            job.Status = JobStatus.Cancelled;
            job.MarkFinished(now);
        }

        await storage.UpdateJobAsync(job);
        if (completedImmediately)
        {
            broadcaster.Complete(id);
            signal.Notify();
        }

        return Results.Ok(job);
    }

    private static async Task<IResult> ShouldCancel(Guid id, HttpContext ctx, IFleetStorage storage)
    {
        if (!TryGetWorkerId(ctx, out var callerWorkerId))
            return Results.Forbid();

        var job = await storage.GetJobAsync(id);

        // Job no longer exists or has reached a terminal state — the worker must
        // abort whatever in-memory state it still holds for it. The coordinator has
        // already moved on; reporting completion would either 404 or rewrite a
        // terminal state.
        if (job is null
            || job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
        {
            return Results.Ok(new
            {
                shouldCancel = false,
                action = nameof(JobAction.Abort)
            });
        }

        // Ownership lost (e.g. reaper failed the job and it was re-queued and claimed
        // by another worker). The current worker must release it.
        if (job.WorkerId is not null && job.WorkerId != callerWorkerId)
        {
            return Results.Ok(new
            {
                shouldCancel = false,
                action = nameof(JobAction.Abort)
            });
        }

        var cancelRequested = job.CancellationRequestedAt is not null;
        return Results.Ok(new
        {
            shouldCancel = cancelRequested,
            action = (cancelRequested ? JobAction.Cancel : JobAction.Continue).ToString()
        });
    }

    private static bool TryGetWorkerId(HttpContext ctx, out Guid workerId)
    {
        var raw = ctx.User.FindFirst("worker_id")?.Value;
        return Guid.TryParse(raw, out workerId);
    }
}
