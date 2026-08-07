using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI.Builder;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

public sealed class EditProjectViewModelTests
{
    static EditProjectViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loads_persisted_solution_test_setting(bool runTestsBeforeDeploy)
    {
        var project = new Project { RunTestsBeforeDeploy = runTestsBeforeDeploy };
        var client = new FleetApiClient(new HttpClientHandler(), new HttpClientHandler());

        var vm = new EditProjectViewModel(
            project,
            client,
            Substitute.For<INavigator>(),
            projectsForRefresh: null);

        vm.RunTestsBeforeDeploy.Should().Be(runTestsBeforeDeploy);
    }
}
