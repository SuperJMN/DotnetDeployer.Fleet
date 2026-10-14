namespace DotnetDeployer.Fleet.WorkerService.Coordinator;

/// <summary>
/// <see cref="HttpClient"/> for uploads and downloads of packages and artifacts. Their duration
/// depends on file size and link speed, so they get a much longer timeout than the short
/// requests that only check whether the coordinator is responsive.
/// </summary>
public sealed class WorkerTransferClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}
