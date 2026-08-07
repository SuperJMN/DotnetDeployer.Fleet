using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.WorkerService.Coordinator;
using DotnetDeployer.Fleet.WorkerService.Execution;
using DotnetDeployer.Fleet.WorkerService.Git;
using DotnetDeployer.Fleet.WorkerService.RepoStorage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace DotnetDeployer.Fleet.WorkerService;

/// <summary>
/// Standalone worker host loop:
///   - heartbeats the coordinator
///   - polls for the next assigned job
///   - clones/fetches the repo, runs DotnetDeployer, streams logs and final status
///   - reports cached repos and evicts old ones to respect the disk budget
/// All coordinator interaction goes through HTTP — there is no shared DB.
/// </summary>
public class RemoteWorkerBackgroundService : BackgroundService
{
    private static readonly string WorkerVersion =
        typeof(RemoteWorkerBackgroundService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RemoteWorkerBackgroundService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private readonly IWorkerJobSource jobSource;
    private readonly IWorkerCoordinatorClient coordinator;
    private readonly WorkerOptions options;
    private readonly ILogger<RemoteWorkerBackgroundService> logger;

    private Guid workerId;
    private string repoStoragePath = "fleet-repos";

    public RemoteWorkerBackgroundService(
        IWorkerJobSource jobSource,
        IWorkerCoordinatorClient coordinator,
        IOptions<WorkerOptions> options,
        ILogger<RemoteWorkerBackgroundService> logger)
    {
        this.jobSource = jobSource;
        this.coordinator = coordinator;
        this.options = options.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (this.options.Id is not { } id)
        {
            logger.LogError("Worker has no Id assigned. Bootstrap should have populated it.");
            return;
        }
        workerId = id;
        repoStoragePath = this.options.RepoStoragePath;
        Directory.CreateDirectory(repoStoragePath);
        RepoStorageIsolator.EnsureBarrierFiles(repoStoragePath);

        logger.LogInformation(
            "Worker {Id} started. Coordinator: {Url}. Repos: {Path} (CPM-isolated)",
            workerId, this.options.CoordinatorBaseUrl, repoStoragePath);

        // Announce ourselves as Online with retries — the coordinator may still
        // be starting up, so we tolerate transient failures.
        await AnnounceOnlineWithRetryAsync(stoppingToken);

        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(this.options.HeartbeatIntervalSeconds));
        using var pollTimer = new PeriodicTimer(TimeSpan.FromSeconds(this.options.PollIntervalSeconds));

        var heartbeatTask = HeartbeatLoopAsync(heartbeatTimer, stoppingToken);
        var pollTask = PollLoopAsync(pollTimer, stoppingToken);

        await Task.WhenAll(heartbeatTask, pollTask);
    }

    private async Task AnnounceOnlineWithRetryAsync(CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await coordinator.UpdateStatusAsync(workerId, WorkerStatus.Online, ct);
                logger.LogInformation("Worker announced as Online (attempt {Attempt})", attempt);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Status announce attempt {Attempt}/{Max} failed", attempt, maxAttempts);
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
        logger.LogError(
            "Could not announce Online after {Max} attempts — heartbeat will retry in the background",
            maxAttempts);
    }

    private async Task HeartbeatLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await coordinator.SendHeartbeatAsync(workerId, WorkerVersion, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Includes TaskCanceledException from per-call HttpClient timeouts —
                // swallow and keep looping so the worker stays alive.
                logger.LogWarning(ex, "Heartbeat failed");
            }
        }
    }

    private async Task PollLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var job = await jobSource.GetNextJobAsync(workerId, ct);
                if (job is null) continue;
                await ExecuteJobAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Includes TaskCanceledException from HttpClient.Timeout. The previous
                // `when (ex is not OperationCanceledException)` filter let those escape
                // and silently killed the poll loop while heartbeats kept the worker
                // marked Online — leaving queued jobs without a claimer until restart.
                logger.LogError(ex, "Error in poll loop");
            }
        }
    }

    private async Task ExecuteJobAsync(DeploymentJob job, CancellationToken ct)
    {
        logger.LogInformation("Starting job {JobId} for project {ProjectId}", job.Id, job.ProjectId);

        await using var logBuffer = new LogChunkBuffer(
            send: (chunk, token) => jobSource.SendLogChunkAsync(job.Id, chunk, token),
            ct: ct);

        async Task Log(string line)
        {
            logger.LogInformation("[Job {Id}] {Line}", job.Id, line);
            await logBuffer.AppendAsync(line);
        }

        async Task<int> UploadPackageArtifactsAsync(string outputDir, CancellationToken token)
        {
            if (!Directory.Exists(outputDir))
                throw new InvalidOperationException("Deployer completed but did not create the package output directory.");

            var files = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
                throw new InvalidOperationException("Deployer completed but did not produce package artifacts.");

            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(outputDir, file).Replace('\\', '/');
                await using var stream = File.OpenRead(file);
                await jobSource.UploadArtifactAsync(job.Id, relativePath, stream, token);

                var size = new FileInfo(file).Length;
                await Log($"[artifact] Uploaded {relativePath} ({size} bytes)");
            }

            return files.Count;
        }

        // Tracks whether the coordinator has told us to abort. When set, we MUST NOT
        // call ReportJobCompletedAsync (or any best-effort variant) afterwards: the
        // coordinator has already moved on and any further write would either 404 or
        // overwrite a terminal state.
        var aborted = false;

        try
        {
            await SetWorkerBusy(true, ct);
            await jobSource.ReportJobStartedAsync(job.Id, workerId, ct);

            var project = await coordinator.GetProjectAsync(job.ProjectId, ct);
            if (project is null)
            {
                await jobSource.ReportJobCompletedAsync(job.Id, false, "Project not found", ct);
                return;
            }

            await ManageDiskSpaceAsync(ct);

            var localPath = Path.Combine(repoStoragePath, RepoDirectoryName.Create(project.Name));
            var branch = string.IsNullOrWhiteSpace(project.Branch) ? "main" : project.Branch;

            using var cancelCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelCts.Token);
            var jobCt = linkedCts.Token;

            // Monitor task is scoped to jobCt — it will end automatically when the job ends
            // OR when the host shuts down. We also keep a reference and await it in `finally`
            // so it can never outlive its job (this used to be fire-and-forget against the
            // host token, which leaked one polling task per job and could pin the worker
            // online-but-idle for hours).
            var monitorTask = MonitorJobActionAsync(job.Id, cancelCts, () => aborted = true, jobCt);

            try
            {
                await Log($"=== DotnetDeployer.Fleet Worker | Job {job.Id} ===");
                await Log($"Project: {project.Name} | Branch: {branch}");
                await Log($"Git URL: {project.GitUrl}");

                // worker.git.clone — the worker emits its own phases for steps that
                // happen BEFORE DotnetDeployer is invoked (clone/fetch). DotnetDeployer
                // emits the rest from inside the build.
                await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.git.clone",
                    attrs: new() { ["branch"] = branch }, ct: jobCt);
                var gitSw = System.Diagnostics.Stopwatch.StartNew();
                bool gitOk = false;
                try
                {
                    await GitHelper.CloneOrFetchAsync(project.GitUrl, branch, localPath,
                        msg => logBuffer.AppendAsync(msg), jobCt, project.GitToken);
                    gitOk = true;
                }
                finally
                {
                    gitSw.Stop();
                    await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.git.clone",
                        status: gitOk ? PhaseStatus.Ok : PhaseStatus.Fail,
                        durationMs: gitSw.ElapsedMilliseconds, ct: jobCt);
                }
                await logBuffer.FlushAsync();

                var cacheSize = GitHelper.GetDirectorySize(localPath);
                try
                {
                    await coordinator.UpsertRepoCacheAsync(workerId, new RepoCache
                    {
                        WorkerId = workerId,
                        ProjectId = project.Id,
                        LocalPath = localPath,
                        SizeBytes = cacheSize,
                        LastUsedAt = DateTimeOffset.UtcNow,
                        LastKnownCommitSha = job.TriggerCommitSha
                    }, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to upsert repo cache metadata.");
                }

                var globalSecrets = await coordinator.GetGlobalSecretsAsync(ct);
                var projectSecrets = await coordinator.GetProjectSecretsAsync(project.Id, ct);

                var envVars = globalSecrets
                    .Concat(projectSecrets)
                    .GroupBy(s => s.Name)
                    .ToDictionary(g => g.Key, g => g.Last().Value);

                var packageOutputDir = job.Kind == JobKind.PackageBuild
                    ? PreparePackageOutputDirectory(job.Id)
                    : null;

                var deployerArguments = BuildDeployerArguments(job, packageOutputDir);

                async Task<(bool Success, string? Error)> RunSolutionTestsAsync(CancellationToken token)
                {
                    await Log("=== Running solution tests ===");
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.solution.test", ct: token);
                    var testSw = System.Diagnostics.Stopwatch.StartNew();
                    (bool Success, string? Error) result = (false, null);

                    try
                    {
                        result = await SolutionTestRunner.RunAsync(
                            localPath,
                            onLine: line => logBuffer.AppendAsync(line),
                            envVars: envVars,
                            ct: token);

                        await Log(result.Success
                            ? "=== Solution tests SUCCEEDED ==="
                            : $"=== Solution tests FAILED: {result.Error} ===");
                        return result;
                    }
                    finally
                    {
                        testSw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.solution.test",
                            status: result.Success ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: testSw.ElapsedMilliseconds, ct: token);
                        await logBuffer.FlushAsync();
                    }
                }

                async Task<(bool Success, string? Error)> RunDeployerAsync(CancellationToken token)
                {
                    await Log(job.Kind == JobKind.PackageBuild
                        ? "=== Invoking DotnetDeployer (package-only) ==="
                        : "=== Invoking DotnetDeployer ===");

                    // worker.deployer.invoke wraps the entire DotnetDeployer process. The
                    // marker stream parsed by DeployerRunner produces nested phases
                    // (version.resolve, package.generate.*, github.release.upload, …)
                    // that the coordinator persists alongside this one.
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.deployer.invoke", ct: token);
                    var deploySw = System.Diagnostics.Stopwatch.StartNew();
                    (bool Success, string? Error) result = (false, null);

                    try
                    {
                        result = await DeployerRunner.RunAsync(
                            localPath,
                            onLine: line => logBuffer.AppendAsync(line),
                            arguments: deployerArguments,
                            envVars: envVars,
                            onPhase: ev => jobSource.PostJobPhaseAsync(job.Id, ev, token),
                            ct: token);
                        return result;
                    }
                    finally
                    {
                        deploySw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.deployer.invoke",
                            status: result.Success ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: deploySw.ElapsedMilliseconds, ct: token);
                    }
                }

                var (success, error) = await WorkerDeploymentPipeline.RunAsync(
                    job,
                    project,
                    RunSolutionTestsAsync,
                    RunDeployerAsync,
                    jobCt);

                await logBuffer.FlushAsync();

                if (success && job.Kind == JobKind.PackageBuild && packageOutputDir is not null)
                {
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.artifacts.upload", ct: jobCt);
                    var uploadSw = System.Diagnostics.Stopwatch.StartNew();
                    var uploadOk = false;

                    try
                    {
                        var uploaded = await UploadPackageArtifactsAsync(packageOutputDir, jobCt);
                        uploadOk = true;
                        await Log($"=== Uploaded {uploaded} package artifact(s) ===");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        success = false;
                        error = ex.Message;
                        await Log($"=== Artifact upload FAILED: {error} ===");
                    }
                    finally
                    {
                        uploadSw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.artifacts.upload",
                            status: uploadOk ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: uploadSw.ElapsedMilliseconds, ct: jobCt);
                    }

                    await logBuffer.FlushAsync();
                }

                if (aborted)
                {
                    logger.LogWarning("Job {JobId} aborted by coordinator instruction; not reporting completion.", job.Id);
                }
                else if (success)
                {
                    await Log(job.Kind == JobKind.PackageBuild
                        ? "=== Package build SUCCEEDED ==="
                        : "=== Deployment SUCCEEDED ===");
                    await jobSource.ReportJobCompletedAsync(job.Id, true, null, ct);
                }
                else
                {
                    await Log(job.Kind == JobKind.PackageBuild
                        ? $"=== Package build FAILED: {error} ==="
                        : $"=== Deployment FAILED: {error} ===");
                    await jobSource.ReportJobCompletedAsync(job.Id, false, error, ct);
                }
            }
            finally
            {
                // Stop the monitor and wait for it to actually exit before the parent
                // method returns. This guarantees no stray polling tasks survive past
                // the job's lifetime.
                if (!cancelCts.IsCancellationRequested)
                {
                    try { await cancelCts.CancelAsync(); } catch { /* already disposed */ }
                }
                try { await monitorTask; } catch { /* monitor swallows everything anyway */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Host shutting down — let it propagate
        }
        catch (OperationCanceledException)
        {
            // Either user-initiated cancellation or coordinator-instructed Abort.
            // Distinguish: on Abort we must remain silent.
            if (aborted)
            {
                logger.LogWarning("Job {JobId} aborted by coordinator instruction; not reporting completion.", job.Id);
            }
            else
            {
                try
                {
                    await Log("=== Deployment CANCELLED by user ===");
                    await logBuffer.FlushAsync();
                }
                catch { /* best effort */ }
                await ReportJobFailedBestEffort(job.Id, "Cancelled by user", ct);
            }
        }
        catch (Exception ex)
        {
            try
            {
                await Log($"[EXCEPTION] {ex.Message}");
                await logBuffer.FlushAsync();
            }
            catch { /* best effort */ }
            if (!aborted)
                await ReportJobFailedBestEffort(job.Id, ex.Message, ct);
        }
        finally
        {
            await SetWorkerBusy(false, ct);
        }
    }

    private async Task ReportJobFailedBestEffort(Guid jobId, string error, CancellationToken ct)
    {
        try
        {
            await jobSource.ReportJobCompletedAsync(jobId, false, error, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to report job {JobId} as failed (error: {Error})", jobId, error);
        }
    }

    private async Task MonitorJobActionAsync(
        Guid jobId,
        CancellationTokenSource cancelCts,
        Action onAbort,
        CancellationToken jobCt)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(jobCt))
            {
                var action = await jobSource.GetJobActionAsync(jobId, jobCt);
                switch (action)
                {
                    case JobAction.Cancel:
                        logger.LogInformation("Cancellation requested for job {JobId}", jobId);
                        await cancelCts.CancelAsync();
                        return;
                    case JobAction.Abort:
                        logger.LogWarning(
                            "Coordinator instructed worker to abort job {JobId} (terminal/orphaned). Releasing slot.",
                            jobId);
                        onAbort();
                        await cancelCts.CancelAsync();
                        return;
                    case JobAction.Continue:
                    default:
                        continue;
                }
            }
        }
        catch (OperationCanceledException) { /* job finished or host shutting down */ }
        catch (Exception ex) { logger.LogWarning(ex, "Job action monitor error for {JobId}", jobId); }
    }

    private async Task ManageDiskSpaceAsync(CancellationToken ct)
    {
        if (!options.MaxDiskUsageBytes.HasValue || options.MaxDiskUsageBytes.Value <= 0) return;
        var budget = options.MaxDiskUsageBytes.Value;

        IReadOnlyList<RepoCache> caches;
        try
        {
            caches = await coordinator.GetRepoCachesAsync(workerId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch repo caches; skipping disk-space management.");
            return;
        }

        var ordered = caches.OrderBy(c => c.LastUsedAt).ToList();
        var totalSize = ordered.Sum(c => c.SizeBytes);

        while (totalSize > budget && ordered.Count > 0)
        {
            var oldest = ordered[0];
            ordered.RemoveAt(0);

            logger.LogInformation("Evicting cached repo {Path} ({SizeMb:F1} MB) to free disk space",
                oldest.LocalPath, oldest.SizeBytes / 1024.0 / 1024);

            try
            {
                if (Directory.Exists(oldest.LocalPath))
                    Directory.Delete(oldest.LocalPath, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete cached repo {Path}", oldest.LocalPath);
            }

            try { await coordinator.DeleteRepoCacheAsync(workerId, oldest.Id, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete cache record {Id}", oldest.Id); }

            totalSize -= oldest.SizeBytes;
        }
    }

    private async Task SetWorkerBusy(bool busy, CancellationToken ct)
    {
        try
        {
            await coordinator.UpdateStatusAsync(
                workerId,
                busy ? WorkerStatus.Busy : WorkerStatus.Online,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update worker status to {Busy}", busy);
        }
    }

    private string PreparePackageOutputDirectory(Guid jobId)
    {
        var path = Path.Combine(repoStoragePath, ".package-artifacts", jobId.ToString("N"));
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        return path;
    }

    private static IReadOnlyList<string> BuildDeployerArguments(DeploymentJob job, string? packageOutputDir)
    {
        if (job.Kind != JobKind.PackageBuild)
            return [];

        if (string.IsNullOrWhiteSpace(packageOutputDir))
            throw new InvalidOperationException("Package output directory is required for package build jobs.");

        var request = PackageBuildRequest.Deserialize(job.PackageRequestJson);
        if (request.Targets.Count == 0)
            throw new InvalidOperationException("Package build job has no package targets.");

        var args = new List<string>
        {
            "--package-only"
        };

        if (!string.IsNullOrWhiteSpace(request.PackageProject))
        {
            args.Add("--package-project");
            args.Add(request.PackageProject);
        }

        foreach (var target in request.Targets)
        {
            args.Add("--package-target");
            args.Add(target.ToDeployerTarget());
        }

        args.Add("--output-dir");
        args.Add(packageOutputDir);

        return args;
    }

    /// <summary>
    /// Sends a worker-side phase event (e.g. <c>worker.git.clone</c>) to the
    /// coordinator. Telemetry-only — failures are swallowed by
    /// <see cref="IWorkerJobSource.PostJobPhaseAsync"/> and never break the build.
    /// </summary>
    private Task EmitPhaseAsync(
        Guid jobId,
        PhaseEventKind kind,
        string name,
        PhaseStatus status = PhaseStatus.Unknown,
        long? durationMs = null,
        Dictionary<string, string>? attrs = null,
        CancellationToken ct = default)
    {
        var ev = new PhaseEvent
        {
            Kind = kind,
            Name = name,
            Status = status,
            DurationMs = durationMs,
            Attrs = attrs ?? new Dictionary<string, string>()
        };
        return jobSource.PostJobPhaseAsync(jobId, ev, ct);
    }
}
