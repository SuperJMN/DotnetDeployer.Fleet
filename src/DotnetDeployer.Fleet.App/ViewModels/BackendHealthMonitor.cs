using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDeployer.Fleet.Api.Client;

namespace DotnetDeployer.Fleet.App.ViewModels;

public enum BackendHealth
{
    Unknown,
    Checking,
    Healthy,
    Unreachable,
    Error
}

public record BackendHealthSnapshot(
    BackendHealth State,
    string? Message,
    DateTimeOffset CheckedAt,
    string? Version,
    TimeSpan? Latency,
    Uri? Endpoint);

public interface IBackendHealthMonitor : IDisposable
{
    IObservable<BackendHealthSnapshot> Snapshots { get; }
    BackendHealthSnapshot Current { get; }
    void Start();
    Task<BackendHealthSnapshot> CheckNowAsync(CancellationToken ct = default);
}

public sealed class BackendHealthMonitor : IBackendHealthMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FleetApiClient _client;
    private readonly HttpClient _probe;
    private readonly BehaviorSubject<BackendHealthSnapshot> _subject;
    private IDisposable? _pollSubscription;
    private bool _disposed;

    public BackendHealthMonitor(FleetApiClient client)
        : this(client, new HttpClientHandler())
    {
    }

    internal BackendHealthMonitor(FleetApiClient client, HttpMessageHandler probeHandler)
    {
        _client = client;
        _probe = new HttpClient(probeHandler) { Timeout = ProbeTimeout };
        _subject = new BehaviorSubject<BackendHealthSnapshot>(
            new BackendHealthSnapshot(BackendHealth.Unknown, null, DateTimeOffset.UtcNow, null, null, _client.BaseAddress));
    }

    public IObservable<BackendHealthSnapshot> Snapshots => _subject.AsObservable();
    public BackendHealthSnapshot Current => _subject.Value;

    public void Start()
    {
        if (_pollSubscription is not null || _disposed) return;

        _pollSubscription = _client.BaseAddressChanges
            .Select(_ => Observable.Timer(TimeSpan.Zero, PollInterval, TaskPoolScheduler.Default))
            .Switch()
            .Select(_ => Observable.FromAsync(ct => CheckNowAsync(ct)))
            .Concat()
            .Subscribe(_ => { }, _ => { /* swallow, errors are surfaced via snapshot */ });
    }

    public async Task<BackendHealthSnapshot> CheckNowAsync(CancellationToken ct = default)
    {
        var endpoint = _client.BaseAddress;
        if (endpoint is null)
        {
            var s = new BackendHealthSnapshot(
                BackendHealth.Unknown, "No endpoint configured", DateTimeOffset.UtcNow, null, null, null);
            _subject.OnNext(s);
            return s;
        }

        // Routine re-checks of a healthy endpoint stay silent so the status bar
        // does not flicker; "Checking…" is only shown when health is not yet known.
        if (!IsHealthy(_subject.Value, endpoint))
        {
            _subject.OnNext(new BackendHealthSnapshot(
                BackendHealth.Checking, "Checking…", DateTimeOffset.UtcNow, _subject.Value.Version, null, endpoint));
        }

        var url = new Uri(endpoint, "health");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            using var response = await _probe.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var snap = new BackendHealthSnapshot(
                    BackendHealth.Error,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    DateTimeOffset.UtcNow, null, sw.Elapsed, endpoint);
                _subject.OnNext(snap);
                return snap;
            }

            string? version = null;
            try
            {
                var body = await response.Content.ReadFromJsonAsync<HealthPayload>(JsonOptions, ct);
                version = body?.Version;
            }
            catch
            {
                // Tolerate non-JSON responses; reachability is what matters.
            }

            var ok = new BackendHealthSnapshot(
                BackendHealth.Healthy, null, DateTimeOffset.UtcNow, version, sw.Elapsed, endpoint);
            _subject.OnNext(ok);
            return ok;
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            var snap = new BackendHealthSnapshot(
                BackendHealth.Unreachable, "Timeout", DateTimeOffset.UtcNow, null, sw.Elapsed, endpoint);
            _subject.OnNext(snap);
            return snap;
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            var snap = new BackendHealthSnapshot(
                BackendHealth.Unreachable, ex.Message, DateTimeOffset.UtcNow, null, sw.Elapsed, endpoint);
            _subject.OnNext(snap);
            return snap;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var snap = new BackendHealthSnapshot(
                BackendHealth.Error, ex.Message, DateTimeOffset.UtcNow, null, sw.Elapsed, endpoint);
            _subject.OnNext(snap);
            return snap;
        }
    }

    private static bool IsHealthy(BackendHealthSnapshot snapshot, Uri endpoint) =>
        snapshot.State == BackendHealth.Healthy && snapshot.Endpoint == endpoint;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollSubscription?.Dispose();
        _probe.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }

    private record HealthPayload(string? Status, string? Service, string? Version, long? UptimeSeconds);
}
