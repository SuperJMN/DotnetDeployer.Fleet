using System.Net;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public sealed class BackendHealthMonitorTests
{
    [Fact]
    public async Task CheckNowAsync_WhenCoordinatorStaysHealthy_ShouldNotReportCheckingAgain()
    {
        var probe = new ProbeHandler(HttpStatusCode.OK);
        using var monitor = CreateMonitor(probe, "http://localhost:5000");
        var states = Record(monitor);

        await monitor.CheckNowAsync();
        await monitor.CheckNowAsync();
        await monitor.CheckNowAsync();

        states.Should().Equal(
            BackendHealth.Unknown,
            BackendHealth.Checking,
            BackendHealth.Healthy,
            BackendHealth.Healthy,
            BackendHealth.Healthy);
    }

    [Fact]
    public async Task CheckNowAsync_WhenPreviousCheckFailed_ShouldReportCheckingBeforeTheResult()
    {
        var probe = new ProbeHandler(HttpStatusCode.ServiceUnavailable);
        using var monitor = CreateMonitor(probe, "http://localhost:5000");
        await monitor.CheckNowAsync();
        var states = Record(monitor);
        probe.Status = HttpStatusCode.OK;

        await monitor.CheckNowAsync();

        states.Should().Equal(BackendHealth.Error, BackendHealth.Checking, BackendHealth.Healthy);
    }

    [Fact]
    public async Task CheckNowAsync_WhenEndpointChangedSinceLastHealthyCheck_ShouldReportChecking()
    {
        var probe = new ProbeHandler(HttpStatusCode.OK);
        var client = CreateClient("http://localhost:5000");
        using var monitor = new BackendHealthMonitor(client, probe);
        await monitor.CheckNowAsync();
        var states = Record(monitor);
        client.SetBaseAddress("http://localhost:6000");

        await monitor.CheckNowAsync();

        states.Should().Equal(BackendHealth.Healthy, BackendHealth.Checking, BackendHealth.Healthy);
    }

    private static BackendHealthMonitor CreateMonitor(HttpMessageHandler probe, string endpoint) =>
        new(CreateClient(endpoint), probe);

    private static FleetApiClient CreateClient(string endpoint)
    {
        var api = new ProbeHandler(HttpStatusCode.OK);
        var client = new FleetApiClient(api, api);
        client.SetBaseAddress(endpoint);
        return client;
    }

    private static List<BackendHealth> Record(IBackendHealthMonitor monitor)
    {
        var states = new List<BackendHealth>();
        monitor.Snapshots.Subscribe(s => states.Add(s.State));
        return states;
    }

    private sealed class ProbeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status));
    }
}
