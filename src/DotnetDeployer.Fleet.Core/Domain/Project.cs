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
    /// Whether deployment jobs must run the repository's root solution tests before
    /// invoking DotnetDeployer. Package build jobs never run this test gate.
    /// </summary>
    public bool RunTestsBeforeDeploy { get; set; } = true;

    public string? LastPolledCommitSha { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
