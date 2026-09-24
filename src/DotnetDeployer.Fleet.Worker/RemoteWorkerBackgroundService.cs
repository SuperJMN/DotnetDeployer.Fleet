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
        this.workerId = this.options.Id ?? Guid.NewGuid();
        this.repoStoragePath = this.options.RepoStoragePath;
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

    internal async Task ExecuteJobAsync(DeploymentJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(repoStoragePath);
        RepoStorageIsolator.EnsureBarrierFiles(repoStoragePath);

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
                await Log($"Project: {project.Name} | Branch: {branch} | CommitSha: {job.TriggerCommitSha}");
                await Log($"Git URL: {project.GitUrl}");

                if (string.IsNullOrWhiteSpace(job.TriggerCommitSha))
                {
                    var msg = "Release job rejected: TriggerCommitSha is missing or empty. An immutable commit SHA is required.";
                    await Log($"=== FAILED: {msg} ===");
                    await logBuffer.FlushAsync();
                    await jobSource.ReportJobCompletedAsync(job.Id, false, msg, ct);
                    return;
                }

                // worker.git.clone — the worker emits its own phases for steps that
                // happen BEFORE DotnetDeployer is invoked (clone/fetch). DotnetDeployer
                // emits the rest from inside the build.
                await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.git.clone",
                    attrs: new() { ["branch"] = branch, ["targetSha"] = job.TriggerCommitSha }, ct: jobCt);
                var gitSw = System.Diagnostics.Stopwatch.StartNew();
                bool gitOk = false;
                try
                {
                    await GitHelper.CloneOrFetchAsync(project.GitUrl, branch, localPath,
                        msg => logBuffer.AppendAsync(msg), jobCt, project.GitToken, job.TriggerCommitSha);
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

                var allSecrets = globalSecrets
                    .Concat(projectSecrets)
                    .GroupBy(s => s.Name)
                    .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

                var configSummary = DeployerYamlReader.ReadConfig(localPath);
                var nugetConfig = configSummary.NuGet;
                var githubConfig = configSummary.GitHub;
                var githubPagesConfig = configSummary.GitHubPages;
                var publishSecretNames = configSummary.PublishSecretNames;
                var signingSecretNames = configSummary.SigningSecretNames;
                string? isolatedGitHubConfig = null;
                if (job.Kind == JobKind.Deploy && nugetConfig.Enabled && githubConfig.Enabled)
                {
                    var originalYamlPath = Path.Combine(localPath, "deployer.yaml");
                    if (!File.Exists(originalYamlPath))
                        originalYamlPath = Path.Combine(localPath, "deployer.yml");
                    if (!File.Exists(originalYamlPath))
                        throw new FileNotFoundException("Cannot isolate GitHub publication: deployer.yaml is missing.");

                    isolatedGitHubConfig = DeployerYamlReader.DisableNuGetPublishing(
                        await File.ReadAllTextAsync(originalYamlPath, jobCt));
                }

                // Solution build and test environments must NOT receive publication secrets OR signing secrets
                var buildScrubKeys = publishSecretNames
                    .Concat(signingSecretNames)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var buildEnvVars = allSecrets
                    .Where(kvp => !buildScrubKeys.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

                // Packaging requires signing secrets (e.g. Android keystore and passwords),
                // but MUST NOT receive publication/push credentials.
                var packScrubKeys = publishSecretNames;

                var packEnvVars = allSecrets
                    .Where(kvp => !packScrubKeys.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

                string? pushApiKey = null;
                if (!string.IsNullOrWhiteSpace(nugetConfig.ApiKeySecretName) && allSecrets.TryGetValue(nugetConfig.ApiKeySecretName, out var keyFromCustomName))
                {
                    pushApiKey = keyFromCustomName;
                }
                else if (allSecrets.TryGetValue("NUGET_API_KEY", out var keyFromDefault))
                {
                    pushApiKey = keyFromDefault;
                }

                string? githubToken = null;
                if (!string.IsNullOrWhiteSpace(githubConfig.TokenSecretName) && allSecrets.TryGetValue(githubConfig.TokenSecretName, out var tokenFromCustomName))
                {
                    githubToken = tokenFromCustomName;
                }
                else if (allSecrets.TryGetValue("GITHUB_TOKEN", out var tokenFromDefault))
                {
                    githubToken = tokenFromDefault;
                }
                else if (allSecrets.TryGetValue("GH_TOKEN", out var tokenFromGh))
                {
                    githubToken = tokenFromGh;
                }

                var isPackageRelease = job.Kind == JobKind.Deploy && (nugetConfig.Enabled || project.ExpectedPackageIds.Count > 0);

                if (isPackageRelease && project.ExpectedPackageIds.Count == 0)
                {
                    var msg = "Package release rejected: project.ExpectedPackageIds is empty. Release jobs with NuGet enabled must declare an explicit expected package inventory.";
                    await Log($"=== FAILED: {msg} ===");
                    await logBuffer.FlushAsync();
                    await jobSource.ReportJobCompletedAsync(job.Id, false, msg, ct);
                    return;
                }

                var packageOutputDir = job.Kind == JobKind.PackageBuild
                    ? PreparePackageOutputDirectory(job.Id)
                    : null;

                var deployerArguments = BuildDeployerArguments(job, packageOutputDir);

                async Task<(bool Success, string? Error)> RunSolutionBuildAsync(CancellationToken token)
                {
                    await Log("=== Running solution build ===");
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.solution.build", ct: token);
                    var buildSw = System.Diagnostics.Stopwatch.StartNew();
                    (bool Success, string? Error) result = (false, null);

                    try
                    {
                        result = await SolutionBuildRunner.RunAsync(
                            localPath,
                            onLine: line => logBuffer.AppendAsync(line),
                            envVars: buildEnvVars,
                            scrubKeys: buildScrubKeys,
                            ct: token);

                        await Log(result.Success
                            ? "=== Solution build SUCCEEDED ==="
                            : $"=== Solution build FAILED: {result.Error} ===");
                        return result;
                    }
                    finally
                    {
                        buildSw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.solution.build",
                            status: result.Success ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: buildSw.ElapsedMilliseconds, ct: token);
                        await logBuffer.FlushAsync();
                    }
                }

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
                            envVars: buildEnvVars,
                            scrubKeys: buildScrubKeys,
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
                            envVars: packEnvVars,
                            onPhase: ev => jobSource.PostJobPhaseAsync(job.Id, ev, token),
                            scrubKeys: packScrubKeys,
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

                async Task<(bool Success, string? Error)> RunGitHubDeployAsync(CancellationToken token)
                {
                    await Log("=== Invoking DotnetDeployer (GitHub deployment) ===");
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.deployer.github", ct: token);
                    var ghSw = System.Diagnostics.Stopwatch.StartNew();
                    (bool Success, string? Error) result = (false, null);

                    string? tempConfigFile = null;
                    try
                    {
                        var deployerArgs = new List<string>(deployerArguments);
                        if (isolatedGitHubConfig is not null)
                        {
                            tempConfigFile = Path.Combine(localPath, $".deployer.no-nuget.{Guid.NewGuid():N}.yaml");
                            await File.WriteAllTextAsync(tempConfigFile, isolatedGitHubConfig, token);
                            deployerArgs.AddRange(["--config", Path.GetFileName(tempConfigFile)]);
                        }

                        var githubPublishEnvVars = new Dictionary<string, string>(packEnvVars, StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(githubToken))
                        {
                            githubPublishEnvVars[githubConfig.TokenSecretName ?? "GITHUB_TOKEN"] = githubToken;
                            githubPublishEnvVars["GITHUB_TOKEN"] = githubToken;
                        }

                        var githubScrubKeys = publishSecretNames
                            .Where(k => !string.Equals(k, "GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase)
                                     && !string.Equals(k, "GH_TOKEN", StringComparison.OrdinalIgnoreCase)
                                     && !string.Equals(k, githubConfig.TokenSecretName, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        result = await DeployerRunner.RunAsync(
                            localPath,
                            onLine: line => logBuffer.AppendAsync(line),
                            arguments: deployerArgs,
                            envVars: githubPublishEnvVars,
                            onPhase: ev => jobSource.PostJobPhaseAsync(job.Id, ev, token),
                            scrubKeys: githubScrubKeys,
                            ct: token);

                        return result;
                    }
                    finally
                    {
                        if (tempConfigFile is not null && File.Exists(tempConfigFile))
                        {
                            try { File.Delete(tempConfigFile); } catch { }
                        }
                        ghSw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.deployer.github",
                            status: result.Success ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: ghSw.ElapsedMilliseconds, ct: token);
                        await logBuffer.FlushAsync();
                    }
                }

                async Task<(bool Success, string? Error, IReadOnlyList<string> ProducedPackagePaths)> RunPackAsync(CancellationToken token)
                {
                    await Log("=== Invoking DotnetDeployer (pack phase) ===");
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.deployer.pack", ct: token);
                    var packSw = System.Diagnostics.Stopwatch.StartNew();
                    (bool Success, string? Error, IReadOnlyList<string> ProducedPackagePaths) result = (false, null, []);

                    try
                    {
                        var nupkgDir = Path.Combine(localPath, "nupkg");
                        if (Directory.Exists(nupkgDir))
                        {
                            foreach (var existing in Directory.GetFiles(nupkgDir, "*.nupkg"))
                            {
                                try { File.Delete(existing); } catch { }
                            }
                        }

                        var packArgs = deployerArguments.Concat(["--dry-run"]).ToList();
                        var deployerResult = await DeployerRunner.RunAsync(
                            localPath,
                            onLine: line => logBuffer.AppendAsync(line),
                            arguments: packArgs,
                            envVars: packEnvVars,
                            onPhase: ev => jobSource.PostJobPhaseAsync(job.Id, ev, token),
                            scrubKeys: packScrubKeys,
                            ct: token);

                        if (!deployerResult.Success)
                        {
                            result = (false, deployerResult.Error, []);
                            return result;
                        }

                        var producedFiles = new List<string>();
                        if (Directory.Exists(nupkgDir))
                        {
                            producedFiles.AddRange(Directory.GetFiles(nupkgDir, "*.nupkg"));
                        }
                        else
                        {
                            var allFound = Directory.GetFiles(localPath, "*.nupkg", SearchOption.AllDirectories)
                                .Where(p => !p.Contains("/bin/") && !p.Contains("\\bin\\") && !p.Contains("/obj/") && !p.Contains("\\obj\\"))
                                .ToList();
                            producedFiles.AddRange(allFound);
                        }

                        result = (true, null, producedFiles);
                        return result;
                    }
                    finally
                    {
                        packSw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.deployer.pack",
                            status: result.Success ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: packSw.ElapsedMilliseconds, ct: token);
                        await logBuffer.FlushAsync();
                    }
                }

                async Task<(bool Success, string? Error)> VerifyInventoryAsync(IReadOnlyList<string> packagePaths, CancellationToken token)
                {
                    await Log("=== Verifying package inventory ===");
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.inventory.verify", ct: token);
                    var verifySw = System.Diagnostics.Stopwatch.StartNew();
                    (bool Success, string? Error) result = (false, null);

                    try
                    {
                        var producedIds = new List<string>();
                        foreach (var path in packagePaths)
                        {
                            try
                            {
                                var packageId = NuGetPackageReader.ReadPackageId(path);
                                producedIds.Add(packageId);
                            }
                            catch (Exception ex)
                            {
                                var err = $"Failed to read package ID from '{Path.GetFileName(path)}': {ex.Message}";
                                await Log($"[inventory] {err}");
                                result = (false, err);
                                return result;
                            }
                        }

                        await Log($"[inventory] Expected packages ({project.ExpectedPackageIds.Count}): {string.Join(", ", project.ExpectedPackageIds)}");
                        await Log($"[inventory] Produced packages ({producedIds.Count}): {string.Join(", ", producedIds)}");

                        var validation = PackageInventoryValidator.Validate(project.ExpectedPackageIds, producedIds);
                        if (!validation.IsValid)
                        {
                            if (validation.MissingIds.Count > 0)
                                await Log($"[inventory] MISSING expected packages: {string.Join(", ", validation.MissingIds)}");
                            if (validation.ExtraIds.Count > 0)
                                await Log($"[inventory] UNEXPECTED packages produced: {string.Join(", ", validation.ExtraIds)}");
                            if (validation.DuplicateIds.Count > 0)
                                await Log($"[inventory] DUPLICATE packages produced: {string.Join(", ", validation.DuplicateIds)}");

                            result = (false, validation.ErrorMessage);
                            return result;
                        }

                        await Log("=== Package inventory verified successfully ===");
                        result = (true, null);
                        return result;
                    }
                    finally
                    {
                        verifySw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.inventory.verify",
                            status: result.Success ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: verifySw.ElapsedMilliseconds, ct: token);
                        await logBuffer.FlushAsync();
                    }
                }

                async Task<(bool Success, string? Error)> RunPushAsync(IReadOnlyList<string> packagePaths, CancellationToken token)
                {
                    await Log("=== Pushing NuGet packages ===");
                    await EmitPhaseAsync(job.Id, PhaseEventKind.Start, "worker.nuget.push",
                        attrs: new() { ["source"] = nugetConfig.Source }, ct: token);
                    var pushSw = System.Diagnostics.Stopwatch.StartNew();
                    (bool Success, string? Error) result = (false, null);

                    try
                    {
                        if (string.IsNullOrWhiteSpace(pushApiKey))
                        {
                            var err = $"NuGet push secret '{nugetConfig.ApiKeySecretName}' not found in configured secrets.";
                            await Log($"[nuget.push] FAILED: {err}");
                            result = (false, err);
                            return result;
                        }

                        foreach (var packagePath in packagePaths)
                        {
                            await Log($"[nuget.push] Pushing {Path.GetFileName(packagePath)} to {nugetConfig.Source}...");
                            var pushResult = await NuGetPackagePusher.PushAsync(
                                localPath,
                                packagePath,
                                pushApiKey,
                                nugetConfig.Source,
                                line => logBuffer.AppendAsync(line),
                                token);

                            if (!pushResult.Success)
                            {
                                await Log($"[nuget.push] FAILED pushing {Path.GetFileName(packagePath)}: {pushResult.Error}");
                                result = (false, pushResult.Error);
                                return result;
                            }

                            await Log($"[nuget.push] Successfully pushed {Path.GetFileName(packagePath)}");
                        }

                        await Log("=== All NuGet packages pushed successfully ===");
                        result = (true, null);
                        return result;
                    }
                    finally
                    {
                        pushSw.Stop();
                        await EmitPhaseAsync(job.Id, PhaseEventKind.End, "worker.nuget.push",
                            status: result.Success ? PhaseStatus.Ok : PhaseStatus.Fail,
                            durationMs: pushSw.ElapsedMilliseconds, ct: token);
                        await logBuffer.FlushAsync();
                    }
                }

                bool success;
                string? error;

                if (isPackageRelease)
                {
                    var releaseResult = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
                        job,
                        project,
                        RunSolutionBuildAsync,
                        RunSolutionTestsAsync,
                        RunPackAsync,
                        VerifyInventoryAsync,
                        nugetConfig.Enabled ? RunPushAsync : null,
                        githubConfig.Enabled ? RunGitHubDeployAsync : null,
                        jobCt);
                    success = releaseResult.Success;
                    error = releaseResult.Error;
                }
                else if (job.Kind == JobKind.Deploy)
                {
                    Func<CancellationToken, Task<(bool Success, string? Error)>> deployAction =
                        githubConfig.Enabled ? RunGitHubDeployAsync : RunDeployerAsync;

                    var standardResult = await WorkerDeploymentPipeline.RunAsync(
                        job,
                        project,
                        RunSolutionBuildAsync,
                        RunSolutionTestsAsync,
                        deployAction,
                        jobCt);
                    success = standardResult.Success;
                    error = standardResult.Error;
                }
                else
                {
                    var packageResult = await WorkerDeploymentPipeline.RunAsync(
                        job,
                        project,
                        RunSolutionBuildAsync,
                        RunSolutionTestsAsync,
                        RunDeployerAsync,
                        jobCt);
                    success = packageResult.Success;
                    error = packageResult.Error;
                }

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
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await jobSource.ReportJobCompletedAsync(jobId, false, error, timeoutCts.Token);
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
            var initialAction = await jobSource.GetJobActionAsync(jobId, jobCt);
            if (initialAction == JobAction.Cancel)
            {
                logger.LogInformation("Cancellation already requested for job {JobId}", jobId);
                await cancelCts.CancelAsync();
                return;
            }
            if (initialAction == JobAction.Abort)
            {
                logger.LogWarning("Coordinator instructed worker to abort job {JobId}. Releasing slot.", jobId);
                onAbort();
                await cancelCts.CancelAsync();
                return;
            }

            var interval = options.JobActionPollIntervalSeconds > 0
                ? TimeSpan.FromSeconds(options.JobActionPollIntervalSeconds)
                : TimeSpan.FromSeconds(3);
            using var timer = new PeriodicTimer(interval);
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
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await coordinator.UpdateStatusAsync(
                workerId,
                busy ? WorkerStatus.Busy : WorkerStatus.Online,
                timeoutCts.Token);
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
    private async Task EmitPhaseAsync(
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
        try
        {
            await jobSource.PostJobPhaseAsync(jobId, ev, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to emit phase event {Phase}", name);
        }
    }
}
