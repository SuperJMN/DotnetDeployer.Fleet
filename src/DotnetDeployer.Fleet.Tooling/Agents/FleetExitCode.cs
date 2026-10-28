namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>
/// Deterministic exit codes of the client commands, so scripts and agents can branch on them.
/// </summary>
public static class FleetExitCode
{
    public const int Success = 0;

    /// <summary>The job (or the check) failed.</summary>
    public const int JobFailed = 1;

    public const int Cancelled = 2;

    /// <summary>A wait ran out of time before the job reached the requested point.</summary>
    public const int Timeout = 3;

    /// <summary>Bad usage, unknown or ambiguous reference, authentication or connection problem.</summary>
    public const int UsageOrAuth = 4;
}

/// <summary>An error the user can fix (bad reference, missing option, no access), reported with <see cref="FleetExitCode.UsageOrAuth"/>.</summary>
public sealed class FleetUsageException(string message) : Exception(message);
