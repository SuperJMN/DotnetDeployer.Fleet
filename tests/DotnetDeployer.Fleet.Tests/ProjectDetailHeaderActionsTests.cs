using System.Reactive.Linq;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectDetailHeaderActionsTests
{
    [Fact]
    public async Task Header_ShouldGiveEveryActionItsOwnIcon()
    {
        using var vm = CreateViewModel();

        var header = (SectionHeader)await vm.Header.FirstAsync();

        header.Actions.Select(action => action.IconCode).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Header_ShouldShowProjectSecretsWithAKeyAndClearBuildsWithASweep()
    {
        using var vm = CreateViewModel();

        var header = (SectionHeader)await vm.Header.FirstAsync();

        var secrets = header.Actions.Should().ContainSingle(action => action.Text == "Project Secrets").Subject;
        secrets.IconCode.Should().Be("mdi-key-outline");
        secrets.Command.Should().BeSameAs(vm.OpenSecretsCommand);
        var clear = header.Actions.Should().ContainSingle(action => action.Text == "Clear Jobs").Subject;
        clear.IconCode.Should().Be("mdi-delete-sweep-outline");
        clear.Command.Should().BeSameAs(vm.ClearFinishedJobsCommand);
    }

    private static ProjectDetailViewModel CreateViewModel()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Fleet", GitUrl = "https://example.test/repo.git", Branch = "master" };
        var handler = new NotFoundHandler();
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");

        return new ProjectDetailViewModel(project, client, Substitute.For<IConnectedFleetClientContext>(),
            Substitute.For<INavigator>(), Substitute.For<IFileSystemPicker>(), Substitute.For<IDialog>(),
            Substitute.For<INotificationService>(), new InMemoryPackageTargetSelectionStore());
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
