using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using ReactiveUI.Builder;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

public class AddProjectViewModelTests
{
    static AddProjectViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

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
    public void Solution_tests_are_enabled_by_default()
    {
        var vm = CreateViewModel();

        vm.RunTestsBeforeDeploy.Should().BeTrue();
    }

    [Fact]
    public async Task TrySaveAsync_ShouldRejectMissingRequiredFields()
    {
        var vm = CreateViewModel();

        var saved = await vm.TrySaveAsync();

        saved.Should().BeFalse();
        vm.Error.Should().Be("Name, Git URL and Branch are required.");
    }

    private static Task<bool> CurrentCanSave(AddProjectViewModel vm) =>
        vm.CanSave.FirstAsync().ToTask();

    private static AddProjectViewModel CreateViewModel() =>
        new(new FleetApiClient(new HttpClientHandler(), new HttpClientHandler()));
}
