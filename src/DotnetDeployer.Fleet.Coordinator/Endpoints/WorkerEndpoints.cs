using static BCrypt.Net.BCrypt;
using DotnetDeployer.Fleet.Coordinator.Auth;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DotnetDeployer.Fleet.Coordinator.Endpoints;

public static class WorkerEndpoints
{
    public static void MapWorkerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workers");

        // Admin-only management
        group.MapGet("/", GetAll).RequireAuthorization("Admin");
        group.MapPut("/{id:guid}/config", UpdateConfig).RequireAuthorization("Admin");
        group.MapDelete("/{id:guid}", Delete).RequireAuthorization("Admin");

        // Bootstrap: anonymous if a valid X-Registration-Token header is supplied,
        // otherwise requires Admin. This lets new workers self-register without
        // an admin pre-flight when an out-of-band token is shared.
        group.MapPost("/register", Register).AllowAnonymous();

        // Worker bootstrap: exchange (workerId, secret) for a JWT carrying Role=Worker.
        group.MapPost("/login", Login).AllowAnonymous();

        // Worker self-service (must present a Worker JWT whose worker_id matches the route id).
        // The liveness filter refreshes LastSeenAt on every authenticated worker request.
        group.MapPost("/{id:guid}/heartbeat", Heartbeat).RequireAuthorization("Worker").AddEndpointFilter<WorkerLivenessFilter>();
        group.MapPost("/{id:guid}/status", UpdateStatus).RequireAuthorization("Worker").AddEndpointFilter<WorkerLivenessFilter>();
        group.MapGet("/me", GetSelf).RequireAuthorization("Worker").AddEndpointFilter<WorkerLivenessFilter>();

        // Repo cache metadata (worker tells coordinator what it has cached, for visibility/eviction).
        group.MapGet("/{id:guid}/repo-caches", GetRepoCaches).RequireAuthorization("Worker").AddEndpointFilter<WorkerLivenessFilter>();
        group.MapPost("/{id:guid}/repo-caches", UpsertRepoCache).RequireAuthorization("Worker").AddEndpointFilter<WorkerLivenessFilter>();
        group.MapDelete("/{id:guid}/repo-caches/{cacheId:guid}", DeleteRepoCache).RequireAuthorization("Worker").AddEndpointFilter<WorkerLivenessFilter>();
    }

    private static async Task<IResult> GetAll(IFleetStorage storage)
    {
        var workers = await storage.GetWorkersAsync();
        var projectNames = (await storage.GetProjectsAsync()).ToDictionary(p => p.Id, p => p.Name);

        var summaries = new List<WorkerSummary>(workers.Count);
        foreach (var w in workers)
        {
            // The job the worker is building right now, so the UI can link a Busy worker to it.
            var running = (await storage.GetActiveJobsForWorkerAsync(w.Id))
                .FirstOrDefault(j => j.Status == JobStatus.Running);

            summaries.Add(new WorkerSummary(
                w.Id, w.Name, w.Status, w.IsEmbedded, w.LastSeenAt,
                MaxDiskUsageGb: w.MaxDiskUsageBytes / (1024.0 * 1024 * 1024),
                w.RepoStoragePath,
                w.Version,
                w.ProcessorCount,
                w.TotalMemoryMb,
                w.OperatingSystem,
                w.Architecture,
                w.CpuModel,
                CurrentJobId: running?.Id,
                CurrentJobProjectName: running is null ? null : projectNames.GetValueOrDefault(running.ProjectId)));
        }

        return Results.Ok(summaries);
    }

    private static async Task<IResult> Register(
        [FromBody] RegisterWorkerRequest req,
        HttpContext httpContext,
        IFleetStorage storage,
        IConfiguration config)
    {
        var isAdmin = httpContext.User.IsInRole("Admin");
        if (!isAdmin)
        {
            var expected = config["Workers:RegistrationToken"];
            var provided = httpContext.Request.Headers["X-Registration-Token"].ToString();
            if (string.IsNullOrEmpty(expected) || provided != expected)
                return Results.Unauthorized();
        }

        var rawSecret = Guid.NewGuid().ToString("N");
        var worker = new Worker
        {
            Name = req.Name,
            SecretHash = HashPassword(rawSecret),
            IsEmbedded = req.IsEmbedded,
            MaxDiskUsageBytes = req.MaxDiskUsageGb.HasValue
                ? (long)(req.MaxDiskUsageGb.Value * 1024 * 1024 * 1024)
                : 10L * 1024 * 1024 * 1024,
            RepoStoragePath = req.RepoStoragePath,
            ProcessorCount = req.ProcessorCount ?? 0,
            TotalMemoryMb = req.TotalMemoryMb ?? 0,
            OperatingSystem = req.OperatingSystem,
            Architecture = req.Architecture,
            CpuModel = req.CpuModel
        };

        await storage.AddWorkerAsync(worker);
        return Results.Created($"/api/workers/{worker.Id}",
            new { workerId = worker.Id, secret = rawSecret });
    }

    private static async Task<IResult> Login(
        [FromBody] WorkerLoginRequest req,
        IFleetStorage storage,
        JwtService jwt)
    {
        var worker = await storage.GetWorkerAsync(req.WorkerId);
        if (worker is null || !Verify(req.Secret, worker.SecretHash))
            return Results.Unauthorized();

        var token = jwt.GenerateWorkerToken(worker);
        return Results.Ok(new { token, workerId = worker.Id, name = worker.Name });
    }

    private static async Task<IResult> Heartbeat(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage,
        [FromBody] HeartbeatRequest? req = null)
    {
        var claimedId = httpContext.User.FindFirst("worker_id")?.Value;
        if (!Guid.TryParse(claimedId, out var tokenWorkerId) || tokenWorkerId != id)
            return Results.Forbid();

        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        worker.LastSeenAt = DateTimeOffset.UtcNow;
        if (worker.Status == WorkerStatus.Offline)
            worker.Status = WorkerStatus.Online;
        if (!string.IsNullOrWhiteSpace(req?.Version))
            worker.Version = req.Version;
        if (req?.ProcessorCount is { } cpuCount && cpuCount > 0)
            worker.ProcessorCount = cpuCount;
        if (req?.TotalMemoryMb is { } memMb && memMb > 0)
            worker.TotalMemoryMb = memMb;
        if (!string.IsNullOrWhiteSpace(req?.OperatingSystem))
            worker.OperatingSystem = req.OperatingSystem;
        if (!string.IsNullOrWhiteSpace(req?.Architecture))
            worker.Architecture = req.Architecture;
        if (!string.IsNullOrWhiteSpace(req?.CpuModel))
            worker.CpuModel = req.CpuModel;
        await storage.UpdateWorkerAsync(worker);
        return Results.Ok();
    }

    private static async Task<IResult> UpdateConfig(Guid id, [FromBody] UpdateWorkerConfigRequest req, IFleetStorage storage)
    {
        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        if (req.MaxDiskUsageGb.HasValue)
            worker.MaxDiskUsageBytes = (long)(req.MaxDiskUsageGb.Value * 1024 * 1024 * 1024);
        if (req.RepoStoragePath is not null)
            worker.RepoStoragePath = req.RepoStoragePath;

        await storage.UpdateWorkerAsync(worker);
        return Results.Ok(worker);
    }

    private static async Task<IResult> Delete(Guid id, IFleetStorage storage, LogBroadcaster broadcaster)
    {
        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        var failedJobs = await storage.FailJobsForWorkerAsync(id, "Worker unregistered by administrator.");
        foreach (var jobId in failedJobs)
            broadcaster.Complete(jobId);

        await storage.DeleteWorkerAsync(id);
        return Results.Ok(new { worker.Id, worker.Name, failedJobs = failedJobs.Count });
    }

    private static async Task<IResult> UpdateStatus(
        Guid id,
        [FromBody] UpdateWorkerStatusRequest req,
        HttpContext httpContext,
        IFleetStorage storage,
        LogBroadcaster broadcaster,
        JobAssignmentSignal signal)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        var worker = await storage.GetWorkerAsync(id);
        if (worker is null) return Results.NotFound();

        var wasOffline = worker.Status == WorkerStatus.Offline;
        worker.Status = req.Status;
        worker.LastSeenAt = DateTimeOffset.UtcNow;
        await storage.UpdateWorkerAsync(worker);

        // Self-heal on (re)start: when a worker announces Online it is, by definition,
        // not running anything. Running rows still owned by it are crash leftovers and
        // must be failed immediately. Assigned rows are queued work, so keep them
        // claimable for this worker instead of failing them during a normal Busy->Online
        // transition.
        if (req.Status == WorkerStatus.Online)
        {
            var failed = await storage.FailRunningJobsForWorkerAsync(id,
                "Worker restarted while running this job.");
            foreach (var jobId in failed)
                broadcaster.Complete(jobId);

            // A previously-offline worker becoming online means new capacity for the
            // scheduler — wake it so any orphaned Queued jobs land here.
            if (wasOffline) signal.Notify();
        }

        return Results.Ok();
    }

    private static async Task<IResult> GetSelf(HttpContext httpContext, IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var workerId))
            return Results.Forbid();

        var worker = await storage.GetWorkerAsync(workerId);
        return worker is null ? Results.NotFound() : Results.Ok(worker);
    }

    private static async Task<IResult> GetRepoCaches(
        Guid id,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        var caches = await storage.GetRepoCachesAsync(id);
        return Results.Ok(caches);
    }

    private static async Task<IResult> UpsertRepoCache(
        Guid id,
        [FromBody] RepoCache cache,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        cache.WorkerId = id;
        await storage.UpsertRepoCacheAsync(cache);
        return Results.Ok(cache);
    }

    private static async Task<IResult> DeleteRepoCache(
        Guid id,
        Guid cacheId,
        HttpContext httpContext,
        IFleetStorage storage)
    {
        if (!TryGetClaimedWorkerId(httpContext, out var claimed) || claimed != id)
            return Results.Forbid();

        await storage.DeleteRepoCacheAsync(cacheId);
        return Results.NoContent();
    }

    private static bool TryGetClaimedWorkerId(HttpContext ctx, out Guid workerId)
    {
        var raw = ctx.User.FindFirst("worker_id")?.Value;
        return Guid.TryParse(raw, out workerId);
    }

    public record RegisterWorkerRequest(
        string Name,
        bool IsEmbedded = false,
        double? MaxDiskUsageGb = null,
        string? RepoStoragePath = null,
        int? ProcessorCount = null,
        long? TotalMemoryMb = null,
        string? OperatingSystem = null,
        string? Architecture = null,
        string? CpuModel = null);
    public record WorkerSummary(
        Guid Id,
        string Name,
        WorkerStatus Status,
        bool IsEmbedded,
        DateTimeOffset? LastSeenAt,
        double MaxDiskUsageGb,
        string? RepoStoragePath,
        string? Version,
        int ProcessorCount,
        long TotalMemoryMb,
        string? OperatingSystem,
        string? Architecture,
        string? CpuModel,
        Guid? CurrentJobId,
        string? CurrentJobProjectName);
    public record UpdateWorkerConfigRequest(double? MaxDiskUsageGb, string? RepoStoragePath);
    public record WorkerLoginRequest(Guid WorkerId, string Secret);
    public record UpdateWorkerStatusRequest(WorkerStatus Status);
    public record HeartbeatRequest(
        string? Version,
        int? ProcessorCount = null,
        long? TotalMemoryMb = null,
        string? OperatingSystem = null,
        string? Architecture = null,
        string? CpuModel = null);
}
