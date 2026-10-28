using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Coordinator.Services;

/// <summary>Joins the releases Fleet prepared with the deploy jobs that published them.</summary>
public static class ProjectReleases
{
    public static IReadOnlyList<ProjectRelease> Build(
        IReadOnlyList<NuGetReleaseSnapshot> snapshots,
        IReadOnlyList<DeploymentJob> jobs)
    {
        return snapshots
            .Select(snapshot =>
            {
                var manifest = snapshot.Manifest;
                // The most recent deploy of the commit is the one whose outcome the release reflects.
                var job = jobs
                    .Where(j => j.Kind == JobKind.Deploy
                                && string.Equals(j.TriggerCommitSha, manifest.CommitSha, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(j => j.EnqueuedAt)
                    .FirstOrDefault();

                var packages = manifest.Packages
                    .Select(package =>
                    {
                        var progress = snapshot.Progress.FirstOrDefault(p =>
                            string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase));
                        return new ProjectReleasePackage(
                            package.Id,
                            package.Version,
                            progress?.State ?? NuGetReleasePackageState.Prepared,
                            progress?.Detail);
                    })
                    .ToList();

                return new ProjectRelease(
                    manifest.Version,
                    manifest.CommitSha,
                    manifest.Source,
                    manifest.PreparedAt,
                    job?.Id,
                    job?.Status,
                    job?.NuGetVerification,
                    packages);
            })
            .OrderByDescending(r => r.PreparedAt)
            .ToList();
    }
}
