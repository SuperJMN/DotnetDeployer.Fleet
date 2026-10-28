using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public sealed class BackendStatusViewModelTests
{
    [Fact]
    public void Shows_the_endpoint_the_app_talks_to()
    {
        using var status = Create(Snapshot(BackendHealth.Healthy, endpoint: new Uri("http://pi:5000/")));

        status.HasEndpoint.Should().BeTrue();
        status.EndpointText.Should().Be("http://pi:5000");
    }

    [Fact]
    public void Says_when_no_endpoint_is_configured()
    {
        using var status = Create(Snapshot(BackendHealth.Unknown, endpoint: null));

        status.HasEndpoint.Should().BeFalse();
        status.EndpointText.Should().Be("Not configured");
    }

    [Fact]
    public void Follows_the_health_of_the_coordinator()
    {
        var snapshots = new Subject<BackendHealthSnapshot>();
        using var status = Create(Snapshot(BackendHealth.Healthy), snapshots);

        snapshots.OnNext(Snapshot(BackendHealth.Unreachable, endpoint: new Uri("http://other:5000/")));

        status.HealthLabel.Should().Be("No connectivity");
        status.HealthTone.Should().Be(Tone.Danger);
        status.EndpointText.Should().Be("http://other:5000");
    }

    [Fact]
    public void Reports_when_app_and_coordinator_versions_differ()
    {
        using var status = Create(Snapshot(BackendHealth.Healthy, version: "1.2.0+abc"), appVersion: "1.1.0+def");

        status.AppDisplayVersion.Should().Be("v1.1.0");
        status.DisplayVersion.Should().Be("v1.2.0");
        status.VersionsDiffer.Should().BeTrue();
        status.VersionMismatchMessage.Should().Contain("v1.1.0").And.Contain("v1.2.0");
    }

    [Fact]
    public void Does_not_report_a_mismatch_for_the_same_release()
    {
        using var status = Create(Snapshot(BackendHealth.Healthy, version: "1.2.0+abc"), appVersion: "1.2.0+def");

        status.VersionsDiffer.Should().BeFalse();
        status.VersionMismatchMessage.Should().BeNull();
    }

    internal static BackendStatusViewModel Create(
        BackendHealthSnapshot current,
        IObservable<BackendHealthSnapshot>? snapshots = null,
        string? appVersion = "1.0.0")
    {
        var monitor = Substitute.For<IBackendHealthMonitor>();
        monitor.Current.Returns(current);
        monitor.Snapshots.Returns(snapshots ?? new Subject<BackendHealthSnapshot>());
        return new BackendStatusViewModel(monitor, appVersion, ImmediateScheduler.Instance);
    }

    internal static BackendHealthSnapshot Snapshot(BackendHealth state, Uri? endpoint = null, string? version = null) =>
        new(state, null, DateTimeOffset.UtcNow, version, null, endpoint);
}
