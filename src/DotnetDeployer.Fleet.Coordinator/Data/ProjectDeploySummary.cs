using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetDeployer.Fleet.Coordinator.Data;

/// <summary>
/// Keeps the last-deploy summary and <see cref="Project.LastActivityAt"/> on each project in
/// step with its finished jobs, so the summary outlives the jobs themselves ("Clear Builds").
/// Every write is a guarded single-statement UPDATE: an older job never replaces the summary
/// of a newer one, and the job already summarised can always refresh it (e.g. when its NuGet
/// verification completes).
/// </summary>
internal static class ProjectDeploySummary
{
    /// <summary>Records <paramref name="job"/> on its project if it has finished; otherwise does nothing.</summary>
    public static async Task RecordAsync(FleetDbContext db, DeploymentJob job, CancellationToken ct = default)
    {
        if (!IsFinished(job) || job.FinishedAt is not { } finishedAt)
            return;

        await db.Projects
            .Where(p => p.Id == job.ProjectId && (p.LastActivityAt == null || p.LastActivityAt < finishedAt))
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.LastActivityAt, finishedAt), ct);

        if (job.Kind != JobKind.Deploy)
            return;

        await db.Projects
            .Where(p => p.Id == job.ProjectId
                        && (p.LastDeployJobId == job.Id || p.LastDeployFinishedAt == null || p.LastDeployFinishedAt <= finishedAt))
            .ExecuteUpdateAsync(set => set
                .SetProperty(p => p.LastDeployJobId, job.Id)
                .SetProperty(p => p.LastDeployFinishedAt, finishedAt)
                .SetProperty(p => p.LastDeployStatus, job.Status)
                .SetProperty(p => p.LastDeployVersion, job.Version)
                .SetProperty(p => p.LastDeployCommitSha, job.TriggerCommitSha)
                .SetProperty(p => p.LastDeployNuGetVerification, job.NuGetVerification), ct);
    }

    /// <summary>
    /// Fills the summary from the finished jobs still stored. Idempotent: projects whose summary
    /// is already current are left unchanged, so it is safe to run on every startup.
    /// </summary>
    public static async Task BackfillAsync(FleetDbContext db, CancellationToken ct = default)
    {
        var finished = await db.DeploymentJobs
            .AsNoTracking()
            .Where(j => (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled)
                        && j.FinishedAt != null)
            .ToListAsync(ct);

        foreach (var jobs in finished.GroupBy(j => j.ProjectId))
        {
            await RecordAsync(db, jobs.MaxBy(j => j.FinishedAt)!, ct);

            if (jobs.Where(j => j.Kind == JobKind.Deploy).MaxBy(j => j.FinishedAt) is { } lastDeploy)
                await RecordAsync(db, lastDeploy, ct);
        }
    }

    private static bool IsFinished(DeploymentJob job) =>
        job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
}
