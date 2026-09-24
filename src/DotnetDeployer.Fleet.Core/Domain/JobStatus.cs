namespace DotnetDeployer.Fleet.Core.Domain;

public enum JobStatus
{
    Queued,
    Assigned,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    AwaitingNuGetIndex
}
