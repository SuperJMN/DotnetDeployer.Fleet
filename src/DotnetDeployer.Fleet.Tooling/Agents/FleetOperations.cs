using System.Net;
using System.Text.RegularExpressions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>When <c>wait</c> stops waiting.</summary>
public enum WaitUntil
{
    /// <summary>The job reached a terminal state (Succeeded, Failed or Cancelled).</summary>
    Finished,

    /// <summary>
    /// The release is published: same stop point as <see cref="Finished"/>, since a release job only
    /// succeeds once the feed accepted every push.
    /// </summary>
    Published,

    /// <summary>Finished and, when it pushed NuGet packages, the background verification is done.</summary>
    Verified
}

/// <summary>
/// Every client operation of the <c>fleet</c> CLI and the <c>fleet mcp</c> server, built on
/// <see cref="FleetApiClient"/>. Reads have no side effects; writes are the methods whose names
/// say so (deploy, ensure, cancel, set, delete).
/// </summary>
public sealed class FleetOperations
{
    private static readonly JobStatus[] TerminalStatuses = [JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled];

    private readonly FleetApiClient api;
    private readonly TimeProvider time;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly NuGetFeedVersions feedVersions;

    public FleetOperations(
        FleetApiClient api,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        NuGetFeedVersions? feedVersions = null)
    {
        this.api = api;
        this.time = time ?? TimeProvider.System;
        this.delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        this.feedVersions = feedVersions ?? new NuGetFeedVersions();
    }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Workers silent for longer than this are reported as disconnected.</summary>
    public TimeSpan DisconnectedAfter { get; init; } = TimeSpan.FromMinutes(2);

    public static bool IsTerminal(JobStatus status) => TerminalStatuses.Contains(status);

    // ── Projects ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProjectView>> ListProjectsAsync(CancellationToken ct = default)
    {
        var projects = await api.GetProjectsAsync();
        var jobs = await api.GetAllJobsAsync();
        return projects.Select(p => ToView(p, jobs)).ToList();
    }

    public async Task<ProjectDetailView> ShowProjectAsync(string reference, CancellationToken ct = default)
    {
        var project = await ResolveProjectAsync(reference);
        var jobs = await api.GetProjectJobsAsync(project.Id);
        var secrets = await ListSecretsAsync(project.Id.ToString(), ct);
        return new ProjectDetailView(
            ToView(project, jobs),
            HasGitToken: !string.IsNullOrWhiteSpace(project.GitToken),
            project.LastPolledCommitSha,
            project.LastPolledAt,
            secrets);
    }

    public async Task<ProjectPreflightReport> CheckProjectAsync(string reference, CancellationToken ct = default)
    {
        var project = await ResolveProjectAsync(reference);
        return await api.GetProjectPreflightAsync(project.Id, ct);
    }

    /// <summary>
    /// Creates the project, or updates only the settings that differ from the requested ones.
    /// Matches an existing project by name, then by Git URL.
    /// </summary>
    public async Task<EnsureProjectResult> EnsureProjectAsync(
        string name,
        string? gitUrl,
        string? branch,
        int? pollingIntervalMinutes,
        bool? runTests,
        IReadOnlyList<string>? expectedPackageIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new FleetUsageException("--name is required.");

        var projects = await api.GetProjectsAsync();
        var existing = FleetReferences.FindProject(projects, name, gitUrl);
        var packages = expectedPackageIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToList();

        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(gitUrl))
                throw new FleetUsageException($"Project '{name}' does not exist yet: --git-url is required to create it.");

            var created = await api.CreateProjectAsync(
                name.Trim(),
                gitUrl.Trim(),
                string.IsNullOrWhiteSpace(branch) ? "master" : branch.Trim(),
                pollingIntervalMinutes ?? 0,
                runTestsBeforeDeploy: runTests,
                expectedPackageIds: packages);
            return new EnsureProjectResult("Created", ["created"], ToView(created, []));
        }

        var changes = new List<string>();
        string? newName = null, newUrl = null, newBranch = null;
        int? newPoll = null;
        bool? newTests = null;
        List<string>? newPackages = null;

        if (!string.Equals(existing.Name, name.Trim(), StringComparison.Ordinal))
        { newName = name.Trim(); changes.Add($"name: {existing.Name} -> {newName}"); }
        if (!string.IsNullOrWhiteSpace(gitUrl) && !string.Equals(existing.GitUrl, gitUrl.Trim(), StringComparison.Ordinal))
        { newUrl = gitUrl.Trim(); changes.Add($"gitUrl: {existing.GitUrl} -> {newUrl}"); }
        if (!string.IsNullOrWhiteSpace(branch) && !string.Equals(existing.Branch, branch.Trim(), StringComparison.Ordinal))
        { newBranch = branch.Trim(); changes.Add($"branch: {existing.Branch} -> {newBranch}"); }
        if (pollingIntervalMinutes is { } poll && poll != existing.PollingIntervalMinutes)
        { newPoll = poll; changes.Add($"pollingIntervalMinutes: {existing.PollingIntervalMinutes} -> {poll}"); }
        if (runTests is { } tests && tests != existing.RunTestsBeforeDeploy)
        { newTests = tests; changes.Add($"runTestsBeforeDeploy: {existing.RunTestsBeforeDeploy} -> {tests}"); }
        if (packages is not null && !packages.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(existing.ExpectedPackageIds.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        { newPackages = packages; changes.Add($"expectedPackageIds: [{string.Join(", ", existing.ExpectedPackageIds)}] -> [{string.Join(", ", packages)}]"); }

        if (changes.Count == 0)
            return new EnsureProjectResult("Unchanged", [], ToView(existing, await api.GetProjectJobsAsync(existing.Id)));

        await api.UpdateProjectAsync(existing.Id, newName, newUrl, newBranch, newPoll,
            runTestsBeforeDeploy: newTests, expectedPackageIds: newPackages);
        var updated = await api.GetProjectAsync(existing.Id) ?? existing;
        return new EnsureProjectResult("Updated", changes, ToView(updated, await api.GetProjectJobsAsync(existing.Id)));
    }

    // ── Deploy ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Queues a deploy of <paramref name="commitSha"/> (default: the branch head). When a deploy of
    /// that commit is already active or finished, nothing is queued unless <paramref name="force"/>.
    /// </summary>
    public async Task<DeployResult> DeployAsync(string projectReference, string? commitSha, bool force, CancellationToken ct = default)
    {
        var project = await ResolveProjectAsync(projectReference);
        var sha = string.IsNullOrWhiteSpace(commitSha)
            ? (await api.GetProjectHeadAsync(project.Id, ct)).CommitSha
            : commitSha.Trim();

        var jobs = await api.GetProjectJobsAsync(project.Id);
        var existing = jobs
            .Where(j => j.Kind == JobKind.Deploy && string.Equals(j.TriggerCommitSha, sha, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.EnqueuedAt)
            .ToList();
        var workers = await TryGetWorkersAsync();
        var existingViews = existing.Select(j => ToView(j, project.Name, workers)).ToList();

        if (!force && existing.Count > 0)
        {
            var active = existing.FirstOrDefault(j => !IsTerminal(j.Status));
            return active is not null
                ? new DeployResult(DeployResult.AlreadyActive, sha, ToView(active, project.Name, workers), existingViews)
                : new DeployResult(DeployResult.AlreadyDeployed, sha, existingViews[0], existingViews);
        }

        var result = await api.EnqueueDeployAsync(project.Id, sha, force, ct);
        return new DeployResult(
            result.Created ? DeployResult.Queued : DeployResult.AlreadyActive,
            sha,
            ToView(result.Job, project.Name, workers),
            existingViews);
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<JobView>> ListJobsAsync(string? projectReference, JobStatus? status, int limit, CancellationToken ct = default)
    {
        var projects = await api.GetProjectsAsync();
        List<DeploymentJob> jobs;
        if (string.IsNullOrWhiteSpace(projectReference))
        {
            jobs = await api.GetAllJobsAsync();
        }
        else
        {
            var project = FleetReferences.ResolveProject(projects, projectReference);
            jobs = await api.GetProjectJobsAsync(project.Id);
        }

        var workers = await TryGetWorkersAsync();
        var names = projects.ToDictionary(p => p.Id, p => p.Name);
        return jobs
            .Where(j => status is null || j.Status == status)
            .OrderByDescending(j => j.EnqueuedAt)
            .Take(Math.Max(1, limit))
            .Select(j => ToView(j, names.GetValueOrDefault(j.ProjectId), workers))
            .ToList();
    }

    public async Task<JobDetailView> ShowJobAsync(string jobReference, CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        var view = await ToViewAsync(job);
        IReadOnlyList<ProjectReleasePackage> packages = [];
        if (job.Kind == JobKind.Deploy && !string.IsNullOrWhiteSpace(job.TriggerCommitSha))
        {
            List<ProjectRelease> releases;
            try
            {
                releases = await api.GetProjectReleasesAsync(job.ProjectId, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                releases = []; // Coordinator older than the releases endpoint.
            }

            packages = releases
                .FirstOrDefault(r => string.Equals(r.CommitSha, job.TriggerCommitSha, StringComparison.OrdinalIgnoreCase))?
                .Packages ?? [];
        }

        return new JobDetailView(view, packages);
    }

    public async Task<IReadOnlyList<PhaseView>> GetPhasesAsync(string jobReference, CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        return (await api.GetJobPhasesAsync(job.Id))
            .Select(p => new PhaseView(p.Name, p.Status, p.StartedAt, p.EndedAt, p.DurationMs, p.Message))
            .ToList();
    }

    /// <summary>
    /// Waits for the job without manual polling. The exit code is 0 when it succeeded, 1 when it
    /// failed, 2 when it was cancelled and 3 when <paramref name="timeout"/> ran out first.
    /// </summary>
    public async Task<WaitResult> WaitAsync(
        string jobReference,
        WaitUntil until,
        TimeSpan? timeout,
        Action<JobView>? progress = null,
        CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        var deadline = timeout is null ? (DateTimeOffset?)null : time.GetUtcNow() + timeout.Value;
        var projectName = (await api.GetProjectAsync(job.ProjectId))?.Name;
        string? lastSignature = null;

        while (true)
        {
            var workers = await TryGetWorkersAsync();
            var view = ToView(job, projectName, workers);
            var signature = $"{view.Status}|{view.CurrentPhase}|{view.NuGetVerification}|{view.WorkerName}";
            if (signature != lastSignature)
            {
                progress?.Invoke(view);
                lastSignature = signature;
            }

            if (IsDone(job, until))
                return new WaitResult(view, until.ToString(), false, ExitCodeFor(job));

            if (deadline is { } limit && time.GetUtcNow() >= limit)
                return new WaitResult(view, until.ToString(), true, FleetExitCode.Timeout);

            var pause = deadline is { } d ? Min(PollInterval, d - time.GetUtcNow()) : PollInterval;
            await delay(pause > TimeSpan.Zero ? pause : TimeSpan.Zero, ct);
            job = await api.GetJobAsync(job.Id) ?? throw new FleetUsageException($"Job {job.Id} no longer exists.");
        }
    }

    internal static bool IsDone(DeploymentJob job, WaitUntil until) =>
        IsTerminal(job.Status)
        && (until != WaitUntil.Verified || job.NuGetVerification != NuGetVerificationStatus.Pending);

    public static int ExitCodeFor(DeploymentJob job) => job.Status switch
    {
        JobStatus.Succeeded => FleetExitCode.Success,
        JobStatus.Cancelled => FleetExitCode.Cancelled,
        _ => FleetExitCode.JobFailed
    };

    public async Task<JobView> CancelJobAsync(string jobReference, CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        await api.CancelJobAsync(job.Id);
        return await ToViewAsync(await api.GetJobAsync(job.Id) ?? job);
    }

    public async Task<IReadOnlyList<PackageArtifact>> ListArtifactsAsync(string jobReference, CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        return await api.GetJobArtifactsAsync(job.Id);
    }

    public async Task<DownloadResult> DownloadArtifactAsync(string jobReference, string relativePath, string? output, CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        var target = Path.GetFullPath(string.IsNullOrWhiteSpace(output)
            ? Path.GetFileName(relativePath.Replace('\\', '/'))
            : output);
        if (Directory.Exists(target))
            target = Path.Combine(target, Path.GetFileName(relativePath.Replace('\\', '/')));

        await using (var source = await api.OpenJobArtifactStreamAsync(job.Id, relativePath, ct))
        await using (var file = File.Create(target))
        {
            await source.CopyToAsync(file, ct);
        }

        return new DownloadResult(job.Id, relativePath, target, new FileInfo(target).Length);
    }

    // ── Logs and diagnosis ───────────────────────────────────────────────────

    /// <summary>
    /// Returns stored log lines and always ends, also for a running job. <paramref name="grep"/>
    /// (a case-insensitive regex) is applied before <paramref name="tail"/>.
    /// </summary>
    public async Task<LogsResult> GetLogsAsync(string jobReference, int? tail, int? since, string? grep, CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        return await GetLogsAsync(job.Id, tail, since, grep, ct);
    }

    private async Task<LogsResult> GetLogsAsync(Guid jobId, int? tail, int? since, string? grep, CancellationToken ct)
    {
        var filter = CreateFilter(grep);
        if (filter is null)
        {
            var page = await LogPageAsync(jobId, since, tail, ct);
            return new LogsResult(page.JobId, page.Status, page.TotalLines, page.Lines);
        }

        var all = await LogPageAsync(jobId, since, null, ct);
        var matching = all.Lines.Where(l => filter.IsMatch(l.Text)).ToList();
        if (tail is >= 0 && matching.Count > tail.Value)
            matching = matching.GetRange(matching.Count - tail.Value, tail.Value);
        return new LogsResult(all.JobId, all.Status, all.TotalLines, matching);
    }

    /// <summary>Streams new log lines until the job reaches a terminal state.</summary>
    public async Task<LogsResult> FollowLogsAsync(
        string jobReference,
        int? tail,
        int? since,
        string? grep,
        Action<JobLogLine> onLine,
        CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        var filter = CreateFilter(grep);
        var first = await GetLogsAsync(job.Id, tail, since, grep, ct);
        foreach (var line in first.Lines) onLine(line);

        var last = Math.Max(since ?? 0, first.TotalLines);
        var status = first.Status;
        var total = first.TotalLines;
        while (!IsTerminal(status))
        {
            await delay(TimeSpan.FromSeconds(2), ct);
            var page = await LogPageAsync(job.Id, last, null, ct);
            foreach (var line in page.Lines.Where(l => filter is null || filter.IsMatch(l.Text)))
                onLine(line);
            last = Math.Max(last, page.TotalLines);
            status = page.Status;
            total = page.TotalLines;
        }

        return new LogsResult(job.Id, status, total, []);
    }

    public async Task<FailureReport> AnalyzeFailureAsync(string jobReference, CancellationToken ct = default)
    {
        var job = await ResolveJobAsync(jobReference);
        var view = await ToViewAsync(job);
        var phases = (await api.GetJobPhasesAsync(job.Id))
            .Select(p => new PhaseView(p.Name, p.Status, p.StartedAt, p.EndedAt, p.DurationMs, p.Message))
            .ToList();
        var lines = (await LogPageAsync(job.Id, null, null, ct)).Lines.Select(l => l.Text).ToList();
        return JobFailureAnalyzer.Analyze(view, phases, lines);
    }

    /// <summary>
    /// Reads a finite log page. Coordinators older than this tool lack the <c>log-lines</c>
    /// endpoint; for them the SSE stream is read until it closes (finished jobs) or goes quiet.
    /// </summary>
    private async Task<JobLogPage> LogPageAsync(Guid jobId, int? since, int? tail, CancellationToken ct)
    {
        try
        {
            return await api.GetJobLogLinesAsync(jobId, since, tail, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var job = await api.GetJobAsync(jobId) ?? throw new FleetUsageException($"No job with id {jobId}.");
            var entries = new List<LogEntry>();
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(TimeSpan.FromSeconds(IsTerminal(job.Status) ? 60 : 5));
            try
            {
                await foreach (var line in api.StreamJobLogsAsync(jobId, quiet.Token))
                    entries.Add(new LogEntry { JobId = jobId, Line = line });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The stream stays open for a running job; what arrived so far is the snapshot.
            }

            return JobLogPage.From(jobId, job.Status, entries, since, tail);
        }
    }

    private static Regex? CreateFilter(string? grep)
    {
        if (string.IsNullOrEmpty(grep)) return null;
        try
        {
            return new Regex(grep, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new FleetUsageException($"Invalid --grep pattern: {ex.Message}");
        }
    }

    // ── Releases ─────────────────────────────────────────────────────────────

    public async Task<ReleasesResult> ListReleasesAsync(string projectReference, bool compareWithFeed, CancellationToken ct = default)
    {
        var project = await ResolveProjectAsync(projectReference);
        var releases = await api.GetProjectReleasesAsync(project.Id, ct);
        var feed = compareWithFeed ? await CompareWithFeedAsync(project, releases, ct) : [];
        return new ReleasesResult(project.Id, project.Name, releases, feed);
    }

    private async Task<IReadOnlyList<FeedComparison>> CompareWithFeedAsync(Project project, IReadOnlyList<ProjectRelease> releases, CancellationToken ct)
    {
        var defaultSource = releases.FirstOrDefault()?.Source ?? NuGetFeedVersions.NuGetOrg;
        var published = releases
            .SelectMany(r => r.Packages.Select(p => (Source: r.Source, Id: p.Id, Version: (string?)p.Version)));
        var pinned = project.ExpectedPackageIds
            .Select(id => (Source: defaultSource, Id: id, Version: (string?)null));
        var bySource = published
            .Concat(pinned)
            .GroupBy(x => (x.Source, Id: x.Id.ToLowerInvariant()));

        var comparisons = new List<FeedComparison>();
        foreach (var group in bySource)
        {
            var packageId = group.First().Id;
            var fleetVersions = group
                .Select(x => x.Version)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            try
            {
                var feedVersionList = await feedVersions.ListAsync(group.Key.Source, packageId, ct);
                var notByFleet = feedVersionList
                    .Where(v => !fleetVersions.Contains(v, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                comparisons.Add(new FeedComparison(packageId, group.Key.Source, fleetVersions, feedVersionList, notByFleet, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException)
            {
                comparisons.Add(new FeedComparison(packageId, group.Key.Source, fleetVersions, [], [], ex.Message));
            }
        }

        return comparisons;
    }

    // ── Workers ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkerView>> ListWorkersAsync(CancellationToken ct = default)
    {
        var workers = await GetWorkersOrExplainAsync();
        var projects = (await api.GetProjectsAsync()).ToDictionary(p => p.Id, p => p.Name);
        var running = (await api.GetAllJobsAsync())
            .Where(j => j.WorkerId is not null && j.Status is JobStatus.Running or JobStatus.Assigned)
            .OrderBy(j => j.Status == JobStatus.Running ? 0 : 1)
            .GroupBy(j => j.WorkerId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var now = time.GetUtcNow();

        return workers
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .Select(w =>
            {
                var job = running.GetValueOrDefault(w.Id);
                var disconnected = w.Status == WorkerStatus.Offline
                                   || w.LastSeenAt is null
                                   || now - w.LastSeenAt.Value > DisconnectedAfter;
                return new WorkerView(w.Id, w.Name, w.Status, disconnected, w.LastSeenAt, w.Version, w.OperatingSystem,
                    w.Architecture, w.ProcessorCount, w.TotalMemoryMb, job?.Id,
                    job is null ? null : projects.GetValueOrDefault(job.ProjectId), w.Environment);
            })
            .ToList();
    }

    // ── Secrets ──────────────────────────────────────────────────────────────

    /// <summary>Names and scope only; values are never returned.</summary>
    public async Task<IReadOnlyList<SecretView>> ListSecretsAsync(string? projectReference, CancellationToken ct = default)
    {
        var globals = (await api.GetSecretsAsync()).Select(s => new SecretView(s.Name, "global", null, s.UpdatedAt));
        if (string.IsNullOrWhiteSpace(projectReference))
            return globals.ToList();

        var project = await ResolveProjectAsync(projectReference);
        var scoped = (await api.GetProjectSecretsAsync(project.Id))
            .Select(s => new SecretView(s.Name, project.Name, project.Id, s.UpdatedAt));
        return globals.Concat(scoped).ToList();
    }

    /// <summary>Creates or replaces a secret. Replacing an existing value requires <paramref name="overwrite"/>.</summary>
    public async Task<SecretSetResult> SetSecretAsync(string name, string? projectReference, string value, bool overwrite, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new FleetUsageException("A secret name is required.");
        if (string.IsNullOrEmpty(value))
            throw new FleetUsageException($"The value for secret '{name}' is empty.");

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(projectReference))
        {
            var existing = (await api.GetSecretsAsync()).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            if (existing is null)
            {
                var created = await api.CreateSecretAsync(name, value);
                return new SecretSetResult("Created", new SecretView(created.Name, "global", null, created.UpdatedAt));
            }

            EnsureOverwrite(name, overwrite);
            await api.UpdateSecretAsync(existing.Id, name, value);
            return new SecretSetResult("Updated", new SecretView(name, "global", null, time.GetUtcNow()));
        }

        var project = await ResolveProjectAsync(projectReference);
        var current = (await api.GetProjectSecretsAsync(project.Id)).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (current is null)
        {
            var created = await api.CreateProjectSecretAsync(project.Id, name, value);
            return new SecretSetResult("Created", new SecretView(created.Name, project.Name, project.Id, created.UpdatedAt));
        }

        EnsureOverwrite(name, overwrite);
        await api.UpdateProjectSecretAsync(project.Id, current.Id, name, value);
        return new SecretSetResult("Updated", new SecretView(name, project.Name, project.Id, time.GetUtcNow()));
    }

    public async Task<SecretView> DeleteSecretAsync(string name, string? projectReference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectReference))
        {
            var secret = (await api.GetSecretsAsync()).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
                         ?? throw new FleetUsageException($"No global secret named '{name}'.");
            await api.DeleteSecretAsync(secret.Id);
            return new SecretView(secret.Name, "global", null, secret.UpdatedAt);
        }

        var project = await ResolveProjectAsync(projectReference);
        var scoped = (await api.GetProjectSecretsAsync(project.Id)).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
                     ?? throw new FleetUsageException($"Project '{project.Name}' has no secret named '{name}'.");
        await api.DeleteProjectSecretAsync(project.Id, scoped.Id);
        return new SecretView(scoped.Name, project.Name, project.Id, scoped.UpdatedAt);
    }

    private static void EnsureOverwrite(string name, bool overwrite)
    {
        if (!overwrite)
            throw new FleetUsageException($"Secret '{name}' already exists. Pass --yes to replace its value.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Project> ResolveProjectAsync(string reference) =>
        FleetReferences.ResolveProject(await api.GetProjectsAsync(), reference);

    private async Task<DeploymentJob> ResolveJobAsync(string reference)
    {
        Guid id;
        if (Guid.TryParse(reference.Trim(), out var parsed))
            id = parsed;
        else
            id = FleetReferences.ResolveJobId((await api.GetAllJobsAsync()).Select(j => j.Id), reference);

        try
        {
            return await api.GetJobAsync(id) ?? throw new FleetUsageException($"No job with id {id}.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FleetUsageException($"No job with id {id}.");
        }
    }

    private async Task<JobView> ToViewAsync(DeploymentJob job)
    {
        var project = await api.GetProjectAsync(job.ProjectId);
        return ToView(job, project?.Name, await TryGetWorkersAsync());
    }

    private async Task<IReadOnlyList<FleetApiClient.WorkerInfo>> TryGetWorkersAsync()
    {
        try
        {
            return await api.GetWorkersAsync();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Older coordinators restrict the worker list to admins; job views simply omit the name.
            return [];
        }
    }

    private async Task<IReadOnlyList<FleetApiClient.WorkerInfo>> GetWorkersOrExplainAsync()
    {
        try
        {
            return await api.GetWorkersAsync();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new FleetUsageException("This account cannot list workers on this coordinator (admin only before Fleet's agent API).");
        }
    }

    private JobView ToView(DeploymentJob job, string? projectName, IReadOnlyList<FleetApiClient.WorkerInfo> workers) =>
        new(job.Id,
            job.ProjectId,
            projectName,
            job.Kind,
            job.Status,
            job.WorkerId,
            workers.FirstOrDefault(w => w.Id == job.WorkerId)?.Name,
            job.TriggerCommitSha,
            job.IsAutoTriggered,
            job.Version,
            job.CurrentPhase,
            job.EnqueuedAt,
            job.StartedAt,
            job.FinishedAt,
            job.GetElapsedDurationMs(time.GetUtcNow()),
            job.NuGetVerification,
            job.ErrorMessage);

    private static ProjectView ToView(Project project, IReadOnlyList<DeploymentJob> jobs)
    {
        var latest = jobs
            .Where(j => j.ProjectId == project.Id)
            .OrderByDescending(j => j.EnqueuedAt)
            .FirstOrDefault();

        var lastDeploy = project.LastDeployJobId is null && project.LastDeployStatus is null
            ? null
            : new LastDeployView(project.LastDeployJobId, project.LastDeployStatus, project.LastDeployVersion,
                project.LastDeployCommitSha, project.LastDeployNuGetVerification, project.LastDeployFinishedAt);

        return new ProjectView(
            project.Id,
            project.Name,
            project.GitUrl,
            project.Branch,
            project.PollingIntervalMinutes,
            project.RunTestsBeforeDeploy,
            project.ExpectedPackageIds,
            lastDeploy,
            latest is null ? null : new JobBriefView(latest.Id, latest.Kind, latest.Status, latest.TriggerCommitSha, latest.Version, latest.EnqueuedAt));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
