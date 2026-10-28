using System.Net;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.App.ViewModels;
using Zafiro.DivineBytes;
using Zafiro.UI;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectViewModelIconTests
{
    private static readonly byte[] Png =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    [Fact]
    public async Task LoadProjectIcon_SetsIconBytesWhenApiReturnsBytes()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App" };
        var client = CreateClient(project.Id, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Png)
        });
        var vm = new ProjectViewModel(
            project,
            client,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            null!,
            Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
            Substitute.For<Zafiro.UI.INotificationService>());

        await vm.LoadProjectIcon();

        vm.HasProjectIcon.Should().BeTrue();
        vm.HasNoProjectIcon.Should().BeFalse();
        vm.ProjectIconBytes.Should().Equal(Png);
    }

    [Fact]
    public async Task LoadProjectIcon_LeavesFallbackWhenApiReturnsNotFound()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App" };
        var client = CreateClient(project.Id, new HttpResponseMessage(HttpStatusCode.NotFound));
        var vm = new ProjectViewModel(
            project,
            client,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            null!,
            Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
            Substitute.For<Zafiro.UI.INotificationService>());

        await vm.LoadProjectIcon();

        vm.HasProjectIcon.Should().BeFalse();
        vm.HasNoProjectIcon.Should().BeTrue();
        vm.ProjectIconBytes.Should().BeNull();
    }

    [Fact]
    public async Task ChangeProjectIcon_UploadsPickedImageAndUpdatesPreview()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App" };
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");
        var picker = Substitute.For<IFileSystemPicker>();
        var file = new NamedByteSource("icon.png", ByteSource.FromBytes(Png));
        picker.PickForOpen(Arg.Any<FileTypeFilter[]>())
            .Returns(Task.FromResult(Result.Success(Maybe.From<INamedByteSource>(file))));
        var vm = new ProjectViewModel(
            project,
            client,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            null!,
            picker,
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
            Substitute.For<Zafiro.UI.INotificationService>());

        await vm.ChangeProjectIcon();

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Put);
        vm.HasProjectIcon.Should().BeTrue();
        vm.ProjectIconBytes.Should().Equal(Png);
    }

    private static FleetApiClient CreateClient(Guid projectId, HttpResponseMessage iconResponse)
    {
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/api/projects/{projectId}/icon"
                ? iconResponse
                : new HttpResponseMessage(HttpStatusCode.NotFound));
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
