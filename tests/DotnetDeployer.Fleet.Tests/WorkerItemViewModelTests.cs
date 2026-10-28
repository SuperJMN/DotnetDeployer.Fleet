using System.Net;
using System.Reactive.Linq;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.App.ViewModels;
using ReactiveUI.Builder;
using Zafiro.UI;

namespace DotnetDeployer.Fleet.Tests;

public class WorkerItemViewModelTests
{
    static WorkerItemViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    private static FleetApiClient.WorkerInfo Info(int cpu, long mem, string? arch)
        => new(
            Id: Guid.NewGuid(),
            Name: "w",
            Status: WorkerStatus.Online,
            IsEmbedded: false,
            LastSeenAt: null,
            MaxDiskUsageGb: 10,
            RepoStoragePath: null,
            Version: null,
            ProcessorCount: cpu,
            TotalMemoryMb: mem,
            OperatingSystem: "Linux",
            Architecture: arch,
            CpuModel: null);

    private static FleetApiClient.WorkerInfo InfoWithVersion(Guid id, string? version)
        => new(
            Id: id,
            Name: "w",
            Status: WorkerStatus.Online,
            IsEmbedded: false,
            LastSeenAt: null,
            MaxDiskUsageGb: 10,
            RepoStoragePath: null,
            Version: version,
            ProcessorCount: 0,
            TotalMemoryMb: 0,
            OperatingSystem: "Linux",
            Architecture: null,
            CpuModel: null);

    [Fact]
    public void CapabilityLabel_renders_cores_memory_and_arch()
    {
        var vm = new WorkerItemViewModel(Info(8, 16 * 1024, "X64"), client: null!, parent: null!, notificationService: null!);
        vm.CapabilityLabel.Should().Be("8 core(s) · 16.0 GB · X64");
    }

    [Fact]
    public void CapabilityLabel_is_empty_when_no_capabilities_reported()
    {
        var vm = new WorkerItemViewModel(Info(0, 0, null), client: null!, parent: null!, notificationService: null!);
        vm.CapabilityLabel.Should().BeEmpty();
    }

    [Fact]
    public void CapabilityLabel_skips_missing_fields()
    {
        var vm = new WorkerItemViewModel(Info(4, 0, null), client: null!, parent: null!, notificationService: null!);
        vm.CapabilityLabel.Should().Be("4 core(s)");
    }

    [Fact]
    public void DisplayVersion_trims_build_metadata_but_keeps_full_worker_version()
    {
        var workerId = Guid.NewGuid();
        var version = "0.1.9+58f1c495e00554b14859456e8557aa98b9b177dd";
        var vm = new WorkerItemViewModel(InfoWithVersion(workerId, version), client: null!, parent: null!, notificationService: null!);

        vm.DisplayVersion.Should().Be("v0.1.9");
        vm.Worker.Version.Should().Be(version);
    }

    [Fact]
    public void Status_is_a_plain_label_with_a_tone()
    {
        var vm = new WorkerItemViewModel(Info(4, 0, null), client: null!, parent: null!, notificationService: null!);

        vm.StatusLabel.Should().Be("Online");
        vm.StatusIcon.Source.Should().StartWith("mdi-");
        vm.StatusTone.Should().Be(Tone.Success);
    }

    [Fact]
    public void Status_follows_worker_updates()
    {
        var worker = Info(4, 0, null);
        var vm = new WorkerItemViewModel(worker, client: null!, parent: null!, notificationService: null!);

        vm.ApplyWorkerUpdate(worker with { Status = WorkerStatus.Offline });

        vm.StatusLabel.Should().Be("Offline");
        vm.StatusTone.Should().Be(Tone.Danger);
    }

    [Fact]
    public void DisplayVersion_updates_when_worker_version_changes()
    {
        var workerId = Guid.NewGuid();
        var vm = new WorkerItemViewModel(InfoWithVersion(workerId, "0.1.9+old"), client: null!, parent: null!, notificationService: null!);
        var updatedVersion = "0.1.10-1+Branch.master.Sha.93bf16a3c8799dffeec6a642f430b3961c1f037e";

        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkerItemViewModel.DisplayVersion))
                seen.Add(vm.DisplayVersion);
        };

        vm.ApplyWorkerUpdate(InfoWithVersion(workerId, updatedVersion));

        vm.DisplayVersion.Should().Be("v0.1.10-1");
        seen.Should().Contain("v0.1.10-1");
    }

    [Fact]
    public async Task Save_is_not_offered_while_the_disk_limit_is_unchanged()
    {
        var vm = new WorkerItemViewModel(Info(4, 0, null), client: null!, parent: null!, notificationService: Substitute.For<INotificationService>());

        (await vm.SaveConfigCommand.CanExecute.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Save_is_offered_once_the_disk_limit_changes()
    {
        var vm = new WorkerItemViewModel(Info(4, 0, null), client: null!, parent: null!, notificationService: Substitute.For<INotificationService>());

        vm.MaxDiskUsageGb = 25;

        (await vm.SaveConfigCommand.CanExecute.FirstAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Save_is_no_longer_offered_after_the_new_limit_is_stored()
    {
        var vm = new WorkerItemViewModel(Info(4, 0, null), CreateClient(), parent: null!, notificationService: Substitute.For<INotificationService>());
        vm.MaxDiskUsageGb = 25;

        await vm.SaveConfigCommand.Execute();

        vm.Worker.MaxDiskUsageGb.Should().Be(25);
        (await vm.SaveConfigCommand.CanExecute.FirstAsync()).Should().BeFalse();
    }

    private static FleetApiClient CreateClient()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");
        return client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }
}
