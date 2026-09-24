namespace DotnetDeployer.Fleet.WorkerService;

public class WorkerOptions
{
    public const string SectionName = "Worker";

    public string CoordinatorBaseUrl { get; set; } = "http://localhost:5000";

    public Guid? Id { get; set; }
    public string? Secret { get; set; }

    public string? Name { get; set; }
    public string? RegistrationToken { get; set; }

    public string RepoStoragePath { get; set; } = "fleet-repos";
    public long? MaxDiskUsageBytes { get; set; }

    public int PollIntervalSeconds { get; set; } = 10;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public double JobActionPollIntervalSeconds { get; set; } = 3.0;
    public int HttpTimeoutSeconds { get; set; } = 30;
    public int TokenRefreshSkewSeconds { get; set; } = 60;

    public string CredentialsFile { get; set; } = "worker.json";
}
