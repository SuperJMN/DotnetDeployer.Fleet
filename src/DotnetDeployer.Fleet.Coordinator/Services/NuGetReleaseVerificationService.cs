using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.Feeds;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDeployer.Fleet.Coordinator.Services;

/// <summary>
/// Confirms, after a release has succeeded, that every package the feed accepted becomes
/// downloadable with the exact bytes Fleet pushed. Indexing delay is not a failure; only a
/// mismatch, or a package that never appears, turns the job into a failed release.
/// </summary>
public sealed class NuGetReleaseVerificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<NuGetReleaseVerificationService> logger,
    NuGetFeedVerifier? verifier = null) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private readonly NuGetFeedVerifier feed = verifier ?? new NuGetFeedVerifier();
    internal TimeSpan VerificationTimeout { get; set; } = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await VerifyPendingAsync(DateTimeOffset.UtcNow, stoppingToken);
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to verify pending NuGet releases");
                await Task.Delay(TickInterval, stoppingToken);
            }
        }
    }

    internal async Task VerifyPendingAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFleetStorage>();
        var releases = scope.ServiceProvider.GetRequiredService<NuGetReleaseStore>();

        var pending = (await storage.GetJobsAsync(ct))
            .Where(j => j.Status == JobStatus.Succeeded && j.NuGetVerification == NuGetVerificationStatus.Pending
                        && !string.IsNullOrWhiteSpace(j.TriggerCommitSha))
            .ToList();

        foreach (var job in pending)
        {
            try
            {
                await VerifyJobAsync(job, now, storage, releases, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Feed outages are retried on the next tick; they say nothing about the release.
                logger.LogWarning(ex, "NuGet verification for job {JobId} could not reach the feed", job.Id);
            }
        }
    }

    private async Task VerifyJobAsync(DeploymentJob job, DateTimeOffset now, IFleetStorage storage,
        NuGetReleaseStore releases, CancellationToken ct)
    {
        var sha = job.TriggerCommitSha!;
        var release = await releases.GetAsync(job.ProjectId, sha, ct);
        if (release is null)
        {
            job.NuGetVerification = null;
            await storage.UpdateJobAsync(job, ct);
            return;
        }

        foreach (var package in release.Manifest.Packages)
        {
            var progress = release.Progress.Single(p => string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase));
            if (progress.State != NuGetReleasePackageState.AwaitingVerification)
                continue;

            var identity = new PackageIdentity(package.Id, package.Version, package.Sha256, package.ContentHash);
            var found = await feed.CheckAsync(release.Manifest.Source, identity, ct);
            switch (found)
            {
                case FeedPackageStatus.Equivalent:
                    release = await releases.SetStateAsync(job.ProjectId, sha, package.Id,
                        NuGetReleasePackageState.Complete, "Exact downloadable package verified", ct);
                    await LogAsync(storage, job, now, $"[nuget.verify] {package.Id} {package.Version}: downloadable and identical to the pushed package.", ct);
                    break;

                case FeedPackageStatus.Conflict:
                    var conflict = $"NuGet verification failed: the downloadable {package.Id} {package.Version} differs from the package Fleet pushed.";
                    await releases.SetStateAsync(job.ProjectId, sha, package.Id,
                        NuGetReleasePackageState.InterventionRequired, conflict, ct);
                    await FailAsync(storage, job, now, conflict, ct);
                    return;

                case FeedPackageStatus.Missing when job.FinishedAt is { } finished && now - finished >= VerificationTimeout:
                    var missing = $"NuGet verification failed: {package.Id} {package.Version} was accepted by the feed but is still not downloadable after {VerificationTimeout.TotalHours:0.#} hours.";
                    await releases.SetStateAsync(job.ProjectId, sha, package.Id,
                        NuGetReleasePackageState.Incomplete, missing, ct);
                    await FailAsync(storage, job, now, missing, ct);
                    return;
            }
        }

        if (release.Progress.All(p => p.State == NuGetReleasePackageState.Complete))
        {
            job.NuGetVerification = NuGetVerificationStatus.Verified;
            await storage.UpdateJobAsync(job, ct);
            await LogAsync(storage, job, now, "[nuget.verify] All NuGet packages are downloadable and verified.", ct);
            logger.LogInformation("NuGet release of job {JobId} verified", job.Id);
        }
    }

    private async Task FailAsync(IFleetStorage storage, DeploymentJob job, DateTimeOffset now, string error, CancellationToken ct)
    {
        job.Status = JobStatus.Failed;
        job.NuGetVerification = NuGetVerificationStatus.Failed;
        job.ErrorMessage = error;
        await storage.UpdateJobAsync(job, ct);
        await LogAsync(storage, job, now, $"=== {error} ===", ct);
        logger.LogWarning("NuGet release of job {JobId} failed verification: {Error}", job.Id, error);
    }

    private static Task LogAsync(IFleetStorage storage, DeploymentJob job, DateTimeOffset now, string line, CancellationToken ct) =>
        storage.AddLogEntriesAsync([new LogEntry { JobId = job.Id, Timestamp = now, Line = line }], ct);
}
