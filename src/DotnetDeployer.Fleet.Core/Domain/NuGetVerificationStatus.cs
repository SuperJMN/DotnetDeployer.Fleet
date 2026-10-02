namespace DotnetDeployer.Fleet.Core.Domain;

/// <summary>
/// Post-release check that every pushed package is downloadable with the exact bytes
/// Fleet sent. It never delays a release: a job succeeds once the feed accepts the push.
/// </summary>
public enum NuGetVerificationStatus
{
    Pending,
    Verified,
    Failed
}
