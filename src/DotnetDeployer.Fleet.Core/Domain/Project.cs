namespace DotnetDeployer.Fleet.Core.Domain;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string GitUrl { get; set; } = "";
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Optional access token for HTTPS git operations against private repositories.
    /// Injected as the password in <c>https://x-access-token:{token}@host/...</c> when present.
    /// </summary>
    public string? GitToken { get; set; }

    /// <summary>Minutes between automatic polling checks. 0 = disabled.</summary>
    public int PollingIntervalMinutes { get; set; } = 0;

    /// <summary>
    /// Whether release jobs (both Deploy and PackageBuild) must run the repository's root
    /// solution tests before invoking packaging or deployer.
    /// </summary>
    public bool RunTestsBeforeDeploy { get; set; } = true;

    /// <summary>
    /// Authoritative list of expected package IDs for package releases.
    /// If empty or missing, a package release job fails closed before pushing to the feed.
    /// </summary>
    public List<string> ExpectedPackageIds { get; set; } = [];

    public string? LastPolledCommitSha { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
