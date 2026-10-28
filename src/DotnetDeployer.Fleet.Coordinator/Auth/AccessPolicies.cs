using System.Security.Claims;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Coordinator.Auth;

/// <summary>
/// Authorization policies that separate reading from changing. Readers (agents with a read-only
/// token) can query everything except secret values; only admins and operators can change state.
/// </summary>
public static class AccessPolicies
{
    /// <summary>Deploy, cancel, create, update or delete. Admins and operators.</summary>
    public const string Write = "Write";

    /// <summary>Any person or agent (not a worker): admins, operators and readers.</summary>
    public const string User = "User";

    public static bool IsReader(ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Reader));

    /// <summary>Removes the values a reader must never see from a project returned by the API.</summary>
    public static Project RedactFor(ClaimsPrincipal principal, Project project)
    {
        if (IsReader(principal))
            project.GitToken = null;
        return project;
    }

    /// <summary>Removes secret values for readers; names and scope stay visible.</summary>
    public static Secret RedactFor(ClaimsPrincipal principal, Secret secret)
    {
        if (IsReader(principal))
            secret.Value = "";
        return secret;
    }
}
