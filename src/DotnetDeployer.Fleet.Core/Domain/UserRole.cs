namespace DotnetDeployer.Fleet.Core.Domain;

public enum UserRole
{
    Admin,
    Operator,

    /// <summary>
    /// Read-only access for agents and dashboards: can query projects, jobs, logs and workers,
    /// but cannot deploy, change anything or read secret values.
    /// </summary>
    Reader
}
