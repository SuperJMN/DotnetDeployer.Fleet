using DotnetDeployer.Fleet.App.ViewModels;
using ReactiveUI.Builder;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

namespace DotnetDeployer.Fleet.Tests;

public sealed class PollingIntervalViewModelTests
{
    static PollingIntervalViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public void Persisted_zero_loads_as_manual_only()
    {
        var vm = new PollingIntervalViewModel(0);

        vm.IsAutomatic.Should().BeFalse();
        vm.ToMinutes().Value.Should().Be(0);
    }

    [Fact]
    public void Persisted_interval_loads_as_automatic()
    {
        var vm = new PollingIntervalViewModel(15);

        vm.IsAutomatic.Should().BeTrue();
        vm.Minutes.Should().Be(15);
        vm.ToMinutes().Value.Should().Be(15);
    }

    [Fact]
    public void Manual_only_saves_zero_whatever_the_minutes()
    {
        var vm = new PollingIntervalViewModel(15) { IsAutomatic = false, Minutes = null };

        vm.ToMinutes().Value.Should().Be(0);
        vm.Error.Should().BeNull();
    }

    [Fact]
    public void Enabling_automatic_polling_proposes_a_valid_interval()
    {
        var vm = new PollingIntervalViewModel(0) { IsAutomatic = true };

        vm.ToMinutes().Value.Should().Be(PollingIntervalViewModel.DefaultMinutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(2.5)]
    public async Task Invalid_automatic_interval_is_reported_and_blocks_saving(double? minutes)
    {
        var vm = new PollingIntervalViewModel(10) { Minutes = (decimal?)minutes };

        vm.ToMinutes().IsFailure.Should().BeTrue();
        vm.Error.Should().NotBeNullOrWhiteSpace();
        (await vm.IsValid.FirstAsync().ToTask()).Should().BeFalse();
    }
}
