using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Api.Client;

public class FleetApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpMessageHandler _httpHandler;
    private readonly HttpMessageHandler _streamingHandler;
    private readonly BehaviorSubject<Uri?> _baseAddressSubject;
    private readonly BehaviorSubject<bool> _authenticatedSubject = new(false);
    private HttpClient http;
    // Long-lived streams (SSE) need an HttpClient with no Timeout, since HttpClient.Timeout
    // covers the entire response — including body reads — even with ResponseHeadersRead.
    private HttpClient streamingHttp;
    private string? token;

    public FleetApiClient(HttpMessageHandler httpHandler, HttpMessageHandler streamingHandler)
        : this(httpHandler, streamingHandler, Observable.Empty<System.Reactive.Unit>())
    {
    }

    public FleetApiClient(HttpMessageHandler httpHandler, HttpMessageHandler streamingHandler, IObservable<System.Reactive.Unit> unauthorizedSignal)
    {
        _httpHandler = httpHandler;
        _streamingHandler = streamingHandler;
        http = CreateHttpClient(httpHandler);
        streamingHttp = CreateStreamingClient(streamingHandler);
        _baseAddressSubject = new BehaviorSubject<Uri?>(null);
        Unauthorized = unauthorizedSignal;
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler, Uri? baseAddress = null) =>
        new(handler, disposeHandler: false) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };

    private static HttpClient CreateStreamingClient(HttpMessageHandler handler, Uri? baseAddress = null) =>
        new(handler, disposeHandler: false) { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Emits whenever the server rejects a request with 401 Unauthorized — meaning the
    /// stored token is no longer valid (expired, signed with a different secret, etc.).
    /// Subscribers should clear any persisted token and return to the login screen.
    /// </summary>
    public IObservable<System.Reactive.Unit> Unauthorized { get; }

    public Uri? BaseAddress => http.BaseAddress;

    /// <summary>
    /// Emits whenever <see cref="BaseAddress"/> changes (and once on subscribe with the current value).
    /// </summary>
    public IObservable<Uri?> BaseAddressChanges => _baseAddressSubject.AsObservable();

    /// <summary>
    /// Emits the current authentication state and any subsequent change. Replays the latest value
    /// to new subscribers, so a VM can subscribe at any time and immediately react to whether the
    /// client is currently authenticated.
    /// </summary>
    public IObservable<bool> AuthenticatedChanges => _authenticatedSubject.AsObservable();

    public void SetBaseAddress(string baseUrl)
    {
        var uri = new Uri(baseUrl.TrimEnd('/') + "/");

        // HttpClient.BaseAddress is immutable after the first request,
        // so we create fresh instances that reuse the same handlers.
        http = CreateHttpClient(_httpHandler, uri);
        streamingHttp = CreateStreamingClient(_streamingHandler, uri);

        if (token is not null)
        {
            var auth = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Authorization = auth;
            streamingHttp.DefaultRequestHeaders.Authorization = auth;
        }

        _baseAddressSubject.OnNext(uri);
    }

    public void SetToken(string jwt)
    {
        token = jwt;
        var auth = new AuthenticationHeaderValue("Bearer", jwt);
        http.DefaultRequestHeaders.Authorization = auth;
        streamingHttp.DefaultRequestHeaders.Authorization = auth;
        _authenticatedSubject.OnNext(true);
    }

    public void ClearToken()
    {
        token = null;
        http.DefaultRequestHeaders.Authorization = null;
        streamingHttp.DefaultRequestHeaders.Authorization = null;
        _authenticatedSubject.OnNext(false);
    }

    public bool IsAuthenticated => token is not null;

    // ── Auth ─────────────────────────────────────────────────────────────────

    public async Task<LoginResponse?> LoginAsync(string username, string password)
    {
        var response = await http.PostAsJsonAsync("/api/auth/login",
            new { username, password }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
    }

    public async Task<SessionResponse?> GetSession()
    {
        using var response = await http.GetAsync("/api/auth/session");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SessionResponse>(JsonOptions);
    }

    // ── Projects ──────────────────────────────────────────────────────────────

    public async Task<List<Project>> GetProjectsAsync() =>
        await http.GetFromJsonAsync<List<Project>>("/api/projects", JsonOptions) ?? [];

    public async Task<Project?> GetProjectAsync(Guid id)
    {
        using var response = await http.GetAsync($"/api/projects/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Project>(JsonOptions);
    }

    public async Task<byte[]?> GetProjectIcon(Guid id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/api/projects/{id}/icon", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task SetProjectIcon(Guid id, byte[] bytes, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "icon", fileName);
        using var response = await http.PutAsync($"/api/projects/{id}/icon", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetProjectIcon(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/api/projects/{id}/icon", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Project> CreateProjectAsync(
        string name,
        string gitUrl,
        string branch,
        int pollingIntervalMinutes = 0,
        string? gitToken = null,
        bool? runTestsBeforeDeploy = null)
    {
        var response = await http.PostAsJsonAsync("/api/projects",
            new { name, gitUrl, branch, pollingIntervalMinutes, gitToken, runTestsBeforeDeploy }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Project>(JsonOptions))!;
    }

    public async Task UpdateProjectAsync(
        Guid id,
        string? name = null,
        string? gitUrl = null,
        string? branch = null,
        int? pollingIntervalMinutes = null,
        string? gitToken = null,
        bool? runTestsBeforeDeploy = null)
    {
        var response = await http.PutAsJsonAsync($"/api/projects/{id}",
            new { name, gitUrl, branch, pollingIntervalMinutes, gitToken, runTestsBeforeDeploy }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProjectAsync(Guid id)
    {
        var response = await http.DeleteAsync($"/api/projects/{id}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<DeploymentJob> EnqueueDeployAsync(Guid projectId)
    {
        var response = await http.PostAsync($"/api/projects/{projectId}/deploy", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeploymentJob>(JsonOptions))!;
    }

    public async Task<DeploymentJob> EnqueuePackageBuildAsync(Guid projectId, PackageBuildRequest request)
    {
        var response = await http.PostAsJsonAsync($"/api/projects/{projectId}/packages", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeploymentJob>(JsonOptions))!;
    }

    public async Task<List<string>> GetPackageProjectsAsync(Guid projectId) =>
        await http.GetFromJsonAsync<List<string>>($"/api/projects/{projectId}/package-projects", JsonOptions) ?? [];

    public async Task<List<DeploymentJob>> GetProjectJobsAsync(Guid projectId) =>
        await http.GetFromJsonAsync<List<DeploymentJob>>($"/api/projects/{projectId}/jobs", JsonOptions) ?? [];

    /// <summary>
    /// Deletes every job for the project that has reached a terminal state
    /// (Succeeded, Failed or Cancelled), along with their logs. Returns the
    /// number of jobs removed.
    /// </summary>
    public async Task<int> DeleteFinishedProjectJobsAsync(Guid projectId)
    {
        var response = await http.DeleteAsync($"/api/projects/{projectId}/jobs/finished");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<DeleteFinishedJobsResult>(JsonOptions);
        return payload?.Deleted ?? 0;
    }

    private record DeleteFinishedJobsResult(int Deleted);

    // ── Jobs ──────────────────────────────────────────────────────────────────

    public async Task<List<DeploymentJob>> GetAllJobsAsync() =>
        await http.GetFromJsonAsync<List<DeploymentJob>>("/api/jobs", JsonOptions) ?? [];

    /// <summary>
    /// Deletes every job that has reached a terminal state (Succeeded, Failed or Cancelled),
    /// along with their logs. Returns the number of jobs removed.
    /// </summary>
    public async Task<int> DeleteFinishedJobsAsync()
    {
        var response = await http.DeleteAsync("/api/jobs/finished");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<DeleteFinishedJobsResult>(JsonOptions);
        return payload?.Deleted ?? 0;
    }

    public async Task<DeploymentJob?> GetJobAsync(Guid id) =>
        await http.GetFromJsonAsync<DeploymentJob>($"/api/jobs/{id}", JsonOptions);

    /// <summary>
    /// Returns the chronological phase timeline for a job (oldest first).
    /// Phases are emitted by the worker (<c>worker.git.clone</c>,
    /// <c>worker.deployer.invoke</c>) and by DotnetDeployer itself
    /// (<c>version.resolve</c>, <c>package.generate.*</c>, …).
    /// </summary>
    public async Task<List<JobPhase>> GetJobPhasesAsync(Guid id) =>
        await http.GetFromJsonAsync<List<JobPhase>>($"/api/jobs/{id}/phases", JsonOptions) ?? [];

    public async Task<List<PackageArtifact>> GetJobArtifactsAsync(Guid id) =>
        await http.GetFromJsonAsync<List<PackageArtifact>>($"/api/jobs/{id}/artifacts", JsonOptions) ?? [];

    public async Task<byte[]> DownloadJobArtifactAsync(Guid id, string relativePath, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"/api/jobs/{id}/artifacts/{EscapeRelativePath(relativePath)}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<Stream> OpenJobArtifactStreamAsync(Guid id, string relativePath, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"/api/jobs/{id}/artifacts/{EscapeRelativePath(relativePath)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new HttpResponseMessageStream(stream, response);
    }

    public async Task CancelJobAsync(Guid jobId)
    {
        var response = await http.PostAsync($"/api/jobs/{jobId}/cancel", null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Returns an async enumerable of log lines streamed via SSE.
    /// Completes when the job finishes or the cancellation token is triggered.
    /// </summary>
    public async IAsyncEnumerable<string> StreamJobLogsAsync(
        Guid jobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in StreamJobEventsAsync(jobId, ct))
        {
            if (evt.Type == SseEventType.Log)
                yield return evt.Data;
        }
    }

    /// <summary>
    /// Returns an async enumerable of SSE events (log lines and status updates).
    /// </summary>
    public async IAsyncEnumerable<SseEvent> StreamJobEventsAsync(
        Guid jobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Use the dedicated streamingHttp (no Timeout) so the SSE connection stays open.
        using var response = await streamingHttp.GetAsync($"/api/jobs/{jobId}/logs",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        string? currentEventType = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            // SSE: skip comment/keep-alive lines (start with ':')
            if (line.Length > 0 && line[0] == ':') continue;

            // Blank line = end of event block; reset event type for next block
            if (line.Length == 0) { currentEventType = null; continue; }

            if (line.StartsWith("event: "))
            {
                currentEventType = line[7..];
                continue;
            }

            if (line.StartsWith("data: "))
            {
                var data = line[6..];
                var type = currentEventType == "status" ? SseEventType.Status : SseEventType.Log;
                yield return new SseEvent(type, data);
            }
        }
    }

    public enum SseEventType { Log, Status }
    public record SseEvent(SseEventType Type, string Data);

    private sealed class HttpResponseMessageStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Sends a request bypassing <c>HttpClient.Timeout</c> so the response body can be streamed indefinitely.
    /// </summary>
    private async Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Linking with a CTS that we never cancel effectively disables the per-call timeout.
        var noTimeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, noTimeoutCts.Token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
    }

    private static string EscapeRelativePath(string relativePath) =>
        string.Join('/',
            relativePath.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

    // ── Workers ───────────────────────────────────────────────────────────────

    public async Task<List<WorkerInfo>> GetWorkersAsync() =>
        await http.GetFromJsonAsync<List<WorkerInfo>>("/api/workers", JsonOptions) ?? [];

    public async Task UpdateWorkerConfigAsync(Guid workerId, double? maxDiskUsageGb = null, string? repoStoragePath = null)
    {
        var response = await http.PutAsJsonAsync($"/api/workers/{workerId}/config",
            new { maxDiskUsageGb, repoStoragePath }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteWorkerAsync(Guid workerId)
    {
        var response = await http.DeleteAsync($"/api/workers/{workerId}");
        response.EnsureSuccessStatusCode();
    }

    public record LoginResponse(string Token, string Username, string Role);
    public record SessionResponse(string Username, string Role);

    public record WorkerInfo(
        Guid Id,
        string Name,
        WorkerStatus Status,
        bool IsEmbedded,
        DateTimeOffset? LastSeenAt,
        double MaxDiskUsageGb,
        string? RepoStoragePath,
        string? Version = null,
        int ProcessorCount = 0,
        long TotalMemoryMb = 0,
        string? OperatingSystem = null,
        string? Architecture = null,
        string? CpuModel = null);

    // ── Secrets ───────────────────────────────────────────────────────────────

    public async Task<List<Secret>> GetSecretsAsync() =>
        await http.GetFromJsonAsync<List<Secret>>("/api/secrets", JsonOptions) ?? [];

    public async Task<Secret> CreateSecretAsync(string name, string value)
    {
        var response = await http.PostAsJsonAsync("/api/secrets", new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Secret>(JsonOptions))!;
    }

    public async Task UpdateSecretAsync(Guid id, string name, string value)
    {
        var response = await http.PutAsJsonAsync($"/api/secrets/{id}", new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteSecretAsync(Guid id)
    {
        var response = await http.DeleteAsync($"/api/secrets/{id}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<Secret>> GetProjectSecretsAsync(Guid projectId) =>
        await http.GetFromJsonAsync<List<Secret>>($"/api/projects/{projectId}/secrets", JsonOptions) ?? [];

    public async Task<Secret> CreateProjectSecretAsync(Guid projectId, string name, string value)
    {
        var response = await http.PostAsJsonAsync($"/api/projects/{projectId}/secrets",
            new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Secret>(JsonOptions))!;
    }

    public async Task UpdateProjectSecretAsync(Guid projectId, Guid secretId, string name, string value)
    {
        var response = await http.PutAsJsonAsync($"/api/projects/{projectId}/secrets/{secretId}",
            new { name, value }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProjectSecretAsync(Guid projectId, Guid secretId)
    {
        var response = await http.DeleteAsync($"/api/projects/{projectId}/secrets/{secretId}");
        response.EnsureSuccessStatusCode();
    }
}
