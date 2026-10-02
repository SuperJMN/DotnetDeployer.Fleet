using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDeployer.Fleet.Coordinator.Services;

/// <summary>
/// Resumes a durable release after an ambiguous push has had time to appear on the feed.
/// The worker is free to run other jobs between attempts.
/// </summary>
public sealed class NuGetReleaseRetryService(
    IServiceScopeFactory scopeFactory,
    JobAssignmentSignal signal,
    ILogger<NuGetReleaseRetryService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(4);
    internal TimeSpan MaxIndexingAge { get; set; } = TimeSpan.FromHours(24);
    internal int MaxAttempts { get; set; } = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RetryIncompleteAsync(DateTimeOffset.UtcNow, stoppingToken);
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scan incomplete NuGet releases for retry");
                await Task.Delay(TickInterval, stoppingToken);
            }
        }
    }

    internal async Task RetryIncompleteAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFleetStorage>();
        var releases = scope.ServiceProvider.GetRequiredService<NuGetReleaseStore>();
        var jobs = await storage.GetJobsAsync(ct);

        foreach (var attempts in jobs.Where(j => j.Kind == JobKind.Deploy && !string.IsNullOrWhiteSpace(j.TriggerCommitSha))
                     .GroupBy(j => (j.ProjectId, Sha: j.TriggerCommitSha!), ReleaseKeyComparer.Instance))
        {
            var latest = attempts.OrderByDescending(j => j.EnqueuedAt).ThenByDescending(j => j.Id).First();
            if (latest.Status is not (JobStatus.Failed or JobStatus.AwaitingNuGetIndex))
                continue;

            try
            {
                var release = await releases.GetAsync(latest.ProjectId, latest.TriggerCommitSha!, ct);
                if (release is null)
                    continue;

                if (latest.Status == JobStatus.AwaitingNuGetIndex)
                {
                    if (release.Progress.Any(p => p.State is NuGetReleasePackageState.Incomplete or NuGetReleasePackageState.InterventionRequired))
                    {
                        latest.Status = JobStatus.Failed;
                        latest.ErrorMessage = "NuGet release requires manual intervention; inspect the durable manifest.";
                        latest.MarkFinished(now);
                        await storage.UpdateJobAsync(latest, ct);
                        continue;
                    }
                    var awaiting = release.Progress
                        .Where(p => p.State == NuGetReleasePackageState.AwaitingIndex)
                        .ToList();
                    if (awaiting.Count > 0 && now - awaiting.Max(p => p.UpdatedAt) < RetryDelay)
                        continue;
                    // A package may have become downloadable just before the deadline.
                    // Require one worker verification after it before declaring failure.
                    if (awaiting.Count > 0 && now - release.Manifest.PreparedAt >= MaxIndexingAge
                        && awaiting.Max(p => p.UpdatedAt) >= release.Manifest.PreparedAt + MaxIndexingAge)
                    {
                        latest.Status = JobStatus.Failed;
                        latest.ErrorMessage = "NuGet indexing did not complete within 24 hours; inspect the feed and retry the durable manifest manually.";
                        latest.MarkFinished(now);
                        await storage.UpdateJobAsync(latest, ct);
                        foreach (var package in release.Progress.Where(p => p.State == NuGetReleasePackageState.AwaitingIndex))
                            await releases.SetStateAsync(latest.ProjectId, latest.TriggerCommitSha!, package.Id,
                                NuGetReleasePackageState.Incomplete, "Automatic indexing wait expired", ct);
                        continue;
                    }
                    if (await storage.GetProjectAsync(latest.ProjectId, ct) is null)
                        continue;

                    latest.Status = JobStatus.Queued;
                    latest.InitialEnqueuedAt ??= latest.EnqueuedAt;
                    latest.EnqueuedAt = now;
                    latest.ErrorMessage = null;
                    await storage.UpdateJobAsync(latest, ct);
                    await storage.AddLogEntriesAsync(
                        [new LogEntry
                        {
                            JobId = latest.Id,
                            Timestamp = now,
                            Line = $"[nuget.release] Retrying durable manifest for {latest.TriggerCommitSha} after NuGet indexing delay."
                        }], ct);
                    signal.Notify();
                    continue;
                }

                if (!release.Progress.Any(p => p.State == NuGetReleasePackageState.AwaitingIndex)
                    || release.Progress.Any(p => p.State == NuGetReleasePackageState.InterventionRequired))
                    continue;

                if (latest.FinishedAt is not { } finishedAt || now - finishedAt < RetryDelay)
                    continue;

                var failedAttempts = attempts.Count(j => j.EnqueuedAt >= release.Manifest.PreparedAt
                    && j.Status == JobStatus.Failed);
                if (failedAttempts >= MaxAttempts)
                {
                    latest.ErrorMessage = $"NuGet indexing did not complete after {failedAttempts} failed attempts; inspect the feed and retry the durable manifest manually.";
                    await storage.UpdateJobAsync(latest, ct);
                    foreach (var package in release.Progress.Where(p => p.State == NuGetReleasePackageState.AwaitingIndex))
                        await releases.SetStateAsync(latest.ProjectId, latest.TriggerCommitSha!, package.Id,
                            NuGetReleasePackageState.Incomplete, "Automatic retries exhausted", ct);
                    continue;
                }
                if (await storage.GetProjectAsync(latest.ProjectId, ct) is null)
                    continue;

                // A manual retry may have arrived since the first read. Never enqueue
                // another attempt while a newer one is already present.
                var current = await storage.GetJobsByProjectAsync(latest.ProjectId, ct);
                if (current.Any(j => string.Equals(j.TriggerCommitSha, latest.TriggerCommitSha,
                        StringComparison.OrdinalIgnoreCase) && j.Id != latest.Id && j.EnqueuedAt >= latest.EnqueuedAt))
                    continue;

                var retry = new DeploymentJob
                {
                    ProjectId = latest.ProjectId,
                    TriggerCommitSha = latest.TriggerCommitSha,
                    IsAutoTriggered = true
                };
                await storage.AddJobAsync(retry, ct);
                await storage.AddLogEntriesAsync(
                    [new LogEntry
                    {
                        JobId = retry.Id,
                        Timestamp = now,
                        Line = $"[nuget.release] Automatic retry of durable manifest for {latest.TriggerCommitSha} after NuGet indexing delay."
                    }], ct);
                logger.LogInformation("Queued durable NuGet release retry {RetryId} after {PreviousId}", retry.Id, latest.Id);
                signal.Notify();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to retry NuGet release for project {ProjectId}", latest.ProjectId);
            }
        }
    }

    private sealed class ReleaseKeyComparer : IEqualityComparer<(Guid ProjectId, string Sha)>
    {
        public static ReleaseKeyComparer Instance { get; } = new();
        public bool Equals((Guid ProjectId, string Sha) x, (Guid ProjectId, string Sha) y) =>
            x.ProjectId == y.ProjectId && string.Equals(x.Sha, y.Sha, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((Guid ProjectId, string Sha) obj) =>
            HashCode.Combine(obj.ProjectId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Sha));
    }
}
