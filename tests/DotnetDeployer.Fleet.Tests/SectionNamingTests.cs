using System.Reactive.Linq;
using System.Reflection;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using Zafiro.UI;
using Zafiro.UI.Shell.Utils;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// A section is called the same in the menu and in its own header, so the user never wonders
/// whether "Secrets" and "Global Secrets" are two different places.
/// </summary>
public sealed class SectionNamingTests
{
    [Fact]
    public async Task The_jobs_section_has_one_name_that_covers_builds_and_deploys()
    {
        using var vm = new BuildsViewModel(
            Substitute.For<IConnectedFleetClientContext>(),
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            Substitute.For<IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
            Substitute.For<INotificationService>());

        var header = (SectionHeader)await vm.Header.FirstAsync();

        MenuName<BuildsViewModel>().Should().Be("Jobs");
        header.Title.Should().Be("Jobs");
        header.Subtitle.Should().Contain("Builds and deploys");
    }

    [Fact]
    public async Task The_secrets_section_has_the_same_name_in_the_menu_and_its_header()
    {
        var handler = new NoContentHandler();
        using var vm = new SecretsViewModel(
            new FleetApiClient(handler, handler),
            Substitute.For<IConnectedFleetClientContext>(),
            Substitute.For<IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
            Substitute.For<INotificationService>());

        var header = (SectionHeader)await vm.Header.FirstAsync();

        header.Title.Should().Be(MenuName<SecretsViewModel>());
    }

    private static string? MenuName<T>() => typeof(T).GetCustomAttribute<SectionAttribute>()!.Name;

    private sealed class NoContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
    }
}
