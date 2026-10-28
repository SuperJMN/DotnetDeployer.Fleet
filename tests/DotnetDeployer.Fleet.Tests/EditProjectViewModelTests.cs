using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Zafiro.UI.Navigation;
using ReactiveUI.Builder;

namespace DotnetDeployer.Fleet.Tests;

public sealed class EditProjectViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loads_persisted_solution_test_setting(bool runTestsBeforeDeploy)
    {
        var project = new Project { RunTestsBeforeDeploy = runTestsBeforeDeploy };
        var client = new FleetApiClient(new HttpClientHandler(), new HttpClientHandler());

        var vm = new EditProjectViewModel(project, client);

        vm.RunTestsBeforeDeploy.Should().Be(runTestsBeforeDeploy);
    }

    [Fact]
    public void Loads_persisted_expected_package_ids()
    {
        var project = new Project
        {
            ExpectedPackageIds = ["Pkg.A", "Pkg.B"]
        };
        var client = new FleetApiClient(new HttpClientHandler(), new HttpClientHandler());

        var vm = new EditProjectViewModel(project, client);

        vm.ExpectedPackageIdsText.Should().Be("Pkg.A, Pkg.B");
    }

    [Fact]
    public void Loads_persisted_polling_interval()
    {
        var project = new Project { PollingIntervalMinutes = 30 };
        var client = new FleetApiClient(new HttpClientHandler(), new HttpClientHandler());

        var vm = new EditProjectViewModel(project, client);

        vm.Polling.IsAutomatic.Should().BeTrue();
        vm.Polling.Minutes.Should().Be(30);
    }

    [Fact]
    public void A_freshly_opened_form_has_no_changes()
    {
        var vm = new EditProjectViewModel(AProject(), AClient());

        vm.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Editing_a_field_leaves_unsaved_changes()
    {
        var vm = new EditProjectViewModel(AProject(), AClient());

        vm.Branch = "develop";

        vm.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void Changing_the_polling_interval_leaves_unsaved_changes()
    {
        var vm = new EditProjectViewModel(AProject(), AClient());

        vm.Polling.IsAutomatic = true;

        vm.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void Restoring_the_original_value_leaves_no_changes()
    {
        var vm = new EditProjectViewModel(AProject(), AClient());

        vm.Name = "Renamed";
        vm.Name = "App";

        vm.HasChanges.Should().BeFalse();
    }

    private static Project AProject() =>
        new() { Id = Guid.NewGuid(), Name = "App", GitUrl = "https://example.test/repo.git", Branch = "master" };

    private static FleetApiClient AClient() => new(new HttpClientHandler(), new HttpClientHandler());
}
