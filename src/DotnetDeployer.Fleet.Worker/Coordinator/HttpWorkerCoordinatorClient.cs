using System.Net.Http.Json;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Bootstrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetDeployer.Fleet.WorkerService.Coordinator;

public class HttpWorkerCoordinatorClient : IWorkerCoordinatorClient
{
    private readonly HttpClient http;
    private readonly ILogger<HttpWorkerCoordinatorClient> logger;

    private readonly WorkerEnvironmentProbe? environmentProbe;

    public HttpWorkerCoordinatorClient(HttpClient http, ILogger<HttpWorkerCoordinatorClient> logger)
    {
        this.http = http;
        this.logger = logger;
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public HttpWorkerCoordinatorClient(HttpClient http, ILogger<HttpWorkerCoordinatorClient> logger, IOptions<WorkerOptions> options)
        : this(http, logger)
    {
        environmentProbe = SharedProbe(options.Value.RepoStoragePath);
    }

    private static readonly object ProbeGate = new();
    private static WorkerEnvironmentProbe? sharedProbe;

    // The typed client is transient; one probe per process keeps the workload listing cached.
    private static WorkerEnvironmentProbe SharedProbe(string repoStoragePath)
    {
        lock (ProbeGate)
            return sharedProbe ??= new WorkerEnvironmentProbe(repoStoragePath);
    }

    public async Task<Worker?> GetSelfAsync(CancellationToken ct = default)
    {
        var resp = await http.GetAsync("/api/workers/me", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Worker>(ct);
    }

    /// <summary>
    /// Per-request hard ceiling for liveness signals. Heartbeats must never
    /// block the worker for longer than this — they exist precisely to detect
    /// stalls, so a stalled heartbeat is worse than no heartbeat.
    /// </summary>
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);

    public async Task SendHeartbeatAsync(Guid workerId, CancellationToken ct = default)
        => await SendHeartbeatAsync(workerId, version: null, ct);

    public async Task SendHeartbeatAsync(Guid workerId, string? version, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HeartbeatTimeout);

        try
        {
            HttpResponseMessage resp;
            if (string.IsNullOrWhiteSpace(version))
            {
                resp = await http.PostAsync($"/api/workers/{workerId}/heartbeat", content: null, cts.Token);
            }
            else
            {
                // Piggyback the host capabilities on every heartbeat. They rarely change, but
                // sending them on each heartbeat means a coordinator that loses its DB still
                // converges to the right scoring within one heartbeat interval.
                var caps = DotnetDeployer.Fleet.WorkerService.Bootstrap.WorkerCapabilityProbe.Detect();
                var payload = new
                {
                    version,
                    processorCount = caps.ProcessorCount,
                    totalMemoryMb = caps.TotalMemoryMb,
                    operatingSystem = caps.OperatingSystem,
                    architecture = caps.Architecture,
                    cpuModel = caps.CpuModel,
                    environment = environmentProbe?.Current()
                };
                resp = await http.PostAsJsonAsync($"/api/workers/{workerId}/heartbeat", payload, cts.Token);
            }

            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("Heartbeat returned {Status}", (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Local timeout, not host shutdown — log and move on so the
            // heartbeat loop keeps ticking on schedule.
            logger.LogWarning("Heartbeat timed out after {Timeout}s", HeartbeatTimeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Heartbeat transport failure");
        }
    }

    public async Task UpdateStatusAsync(Guid workerId, WorkerStatus status, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/api/workers/{workerId}/status", new { status }, ct);
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("UpdateStatus returned {Status}", (int)resp.StatusCode);
    }

    public async Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/projects/{projectId}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Project>(ct);
    }

    public async Task<IReadOnlyList<Secret>> GetGlobalSecretsAsync(CancellationToken ct = default)
    {
        var resp = await http.GetAsync("/api/secrets", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<Secret>>(ct) ?? new();
    }

    public async Task<IReadOnlyList<Secret>> GetProjectSecretsAsync(Guid projectId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/projects/{projectId}/secrets", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<Secret>>(ct) ?? new();
    }

    public async Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/workers/{workerId}/repo-caches", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<RepoCache>>(ct) ?? new();
    }

    public async Task UpsertRepoCacheAsync(Guid workerId, RepoCache cache, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/api/workers/{workerId}/repo-caches", cache, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteRepoCacheAsync(Guid workerId, Guid cacheId, CancellationToken ct = default)
    {
        var resp = await http.DeleteAsync($"/api/workers/{workerId}/repo-caches/{cacheId}", ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
    }
}
