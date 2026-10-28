using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectsViewModelConnectionTests
{
    [Fact]
    public async Task RefreshCommand_ShouldLoadProjectsAfterRequireSucceeds()
    {
        var projectId = Guid.NewGuid();
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new Project { Id = projectId, Name = "Fleet", GitUrl = "https://example.test/repo.git", Branch = "main" }
            })
        });
        client.SetBaseAddress("http://localhost:5000");
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(client))));
        var vm = new ProjectsViewModel(
            context,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
            Substitute.For<Zafiro.UI.INotificationService>(),
            new InMemoryPackageTargetSelectionStore(), new InMemorySettingsService());

        await vm.RefreshCommand.Execute().FirstAsync();

        vm.Projects.Should().ContainSingle();
        vm.Projects[0].Project.Id.Should().Be(projectId);
    }

    [Fact]
    public async Task AddProjectCommand_WhenRequireFails_ShouldNotify()
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Failure<FleetApiClient>("Cannot connect"))));
        var notificationService = Substitute.For<Zafiro.UI.INotificationService>();
        var notificationShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notificationService.Show(Arg.Any<string>(), Arg.Any<Maybe<string>>())
            .Returns(_ =>
            {
                notificationShown.TrySetResult();
                return Task.CompletedTask;
            });
        var vm = new ProjectsViewModel(
            context,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
            notificationService,
            new InMemoryPackageTargetSelectionStore(), new InMemorySettingsService());

        await vm.AddProjectCommand.Execute().FirstAsync();
        await notificationShown.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await notificationService.Received(1).Show("Cannot connect", Maybe.From("Cannot add project"));
    }

    private static FleetApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        var handler = new StubHandler(response);
        return new FleetApiClient(handler, handler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
