using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

public class AddProjectViewModelTests
{
    [Fact]
    public async Task CanSave_ShouldRequireNameGitUrlAndBranch()
    {
        var vm = CreateViewModel();

        (await CurrentCanSave(vm)).Should().BeFalse();

        vm.Name = "App";
        vm.GitUrl = "https://github.com/example/app.git";
        vm.Branch = "main";

        (await CurrentCanSave(vm)).Should().BeTrue();

        vm.GitUrl = " ";

        (await CurrentCanSave(vm)).Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_polling_interval_blocks_saving_with_an_error()
    {
        var vm = CreateViewModel();
        vm.Name = "App";
        vm.GitUrl = "https://github.com/example/app.git";
        vm.Branch = "main";
        vm.Polling.IsAutomatic = true;
        vm.Polling.Minutes = null;

        (await CurrentCanSave(vm)).Should().BeFalse();

        var saved = await vm.TrySaveAsync();

        saved.Should().BeFalse();
        vm.Error.Should().Be(vm.Polling.Error);
    }

    [Fact]
    public void New_projects_are_manual_only_by_default()
    {
        var vm = CreateViewModel();

        vm.Polling.IsAutomatic.Should().BeFalse();
    }

    [Fact]
    public void Solution_tests_are_enabled_by_default()
    {
        var vm = CreateViewModel();

        vm.RunTestsBeforeDeploy.Should().BeTrue();
    }

    [Fact]
    public void Expected_package_ids_text_is_empty_by_default()
    {
        var vm = CreateViewModel();

        vm.ExpectedPackageIdsText.Should().BeEmpty();
    }

    [Fact]
    public async Task TrySaveAsync_ShouldRejectMissingRequiredFields()
    {
        var vm = CreateViewModel();

        var saved = await vm.TrySaveAsync();

        saved.Should().BeFalse();
        vm.Error.Should().Be("Name, Git URL and Branch are required.");
    }

    [Fact]
    public void A_new_form_has_no_changes()
    {
        var vm = CreateViewModel();

        vm.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Typing_in_the_form_leaves_unsaved_changes()
    {
        var vm = CreateViewModel();

        vm.Name = "App";

        vm.HasChanges.Should().BeTrue();
    }

    private static Task<bool> CurrentCanSave(AddProjectViewModel vm) =>
        vm.CanSave.FirstAsync().ToTask();

    private static AddProjectViewModel CreateViewModel() =>
        new(new FleetApiClient(new HttpClientHandler(), new HttpClientHandler()));
}
