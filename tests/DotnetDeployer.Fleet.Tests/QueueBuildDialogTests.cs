using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI.Builder;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

public sealed class QueueBuildDialogTests
{
    static QueueBuildDialogTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public void Android_formats_are_offered_without_an_architecture()
    {
        var options = Options(Guid.NewGuid(), new InMemoryPackageTargetSelectionStore());

        var android = Platform(options, "Android");

        android.Formats.Should().OnlyContain(format => !format.HasArchitectureChoice);
        android.Targets.Select(target => target.Label).Should().Equal("APK", "AAB");
    }

    [Fact]
    public void Android_targets_are_still_queued_with_an_architecture_the_deployer_accepts()
    {
        var options = Options(Guid.NewGuid(), new InMemoryPackageTargetSelectionStore());
        Platform(options, "Windows").AreAllSelected = false;
        Platform(options, "Android").AreAllSelected = true;

        var request = options.CreateRequest();

        request!.Targets.Select(target => target.ToDeployerTarget()).Should().Equal("apk:x64", "aab:x64");
    }

    [Fact]
    public void Selecting_a_whole_platform_selects_every_format_and_architecture_of_it()
    {
        var options = Options(Guid.NewGuid(), new InMemoryPackageTargetSelectionStore());
        var linux = Platform(options, "Linux");

        linux.AreAllSelected = true;

        linux.Targets.Should().OnlyContain(target => target.IsSelected);
        linux.AreAllSelected.Should().BeTrue();
    }

    [Fact]
    public void Clearing_a_whole_platform_deselects_every_target_of_it()
    {
        var options = Options(Guid.NewGuid(), new InMemoryPackageTargetSelectionStore());
        var windows = Platform(options, "Windows");

        windows.AreAllSelected = false;

        windows.Targets.Should().OnlyContain(target => !target.IsSelected);
        options.SelectedPackageTargetCount.Should().Be(0);
    }

    [Fact]
    public void A_partly_selected_platform_reports_an_undetermined_selection()
    {
        var options = Options(Guid.NewGuid(), new InMemoryPackageTargetSelectionStore());

        Platform(options, "Windows").AreAllSelected.Should().BeNull();
        Platform(options, "macOS").AreAllSelected.Should().BeFalse();
    }

    [Fact]
    public async Task Queuing_a_build_remembers_the_selection_for_the_next_build_of_that_project()
    {
        var project = AProject();
        var store = new InMemoryPackageTargetSelectionStore();
        using var first = ProjectDetail(project, store);
        Platform(first.BuildOptions, "Windows").AreAllSelected = false;
        Platform(first.BuildOptions, "Android").AreAllSelected = true;

        await first.BuildPackagesCommand.Execute().FirstAsync();

        using var next = ProjectDetail(project, store);
        SelectedTargets(next.BuildOptions).Should().Equal("apk:x64", "aab:x64");
    }

    [Fact]
    public async Task A_remembered_selection_does_not_leak_into_other_projects()
    {
        var store = new InMemoryPackageTargetSelectionStore();
        using var first = ProjectDetail(AProject(), store);
        Platform(first.BuildOptions, "Android").AreAllSelected = true;

        await first.BuildPackagesCommand.Execute().FirstAsync();

        using var other = ProjectDetail(AProject(), store);
        SelectedTargets(other.BuildOptions).Should().Equal("exe-setup:x64");
    }

    [Fact]
    public void A_remembered_selection_with_no_known_targets_keeps_the_defaults()
    {
        var projectId = Guid.NewGuid();
        var store = new InMemoryPackageTargetSelectionStore();
        store.Save(projectId, ["zip:sparc"]);

        var options = Options(projectId, store);

        SelectedTargets(options).Should().Equal("exe-setup:x64");
    }

    private static PackageBuildOptionsViewModel Options(Guid projectId, IPackageTargetSelectionStore store) =>
        new(projectId, Substitute.For<IConnectedFleetClientContext>(), store);

    private static PackagePlatformViewModel Platform(PackageBuildOptionsViewModel options, string name) =>
        options.PackagePlatforms.Single(platform => platform.Name == name);

    private static IEnumerable<string> SelectedTargets(PackageBuildOptionsViewModel options) =>
        options.PackagePlatforms.SelectMany(p => p.Targets).Where(t => t.IsSelected).Select(t => t.Key);

    private static Project AProject() =>
        new() { Id = Guid.NewGuid(), Name = "App", GitUrl = "https://example.test/repo.git", Branch = "master" };

    private static ProjectDetailViewModel ProjectDetail(Project project, IPackageTargetSelectionStore store)
    {
        var client = CoordinatorThatAcceptsBuilds(project.Id);
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(client))));
        return new ProjectDetailViewModel(project, client, context, Substitute.For<INavigator>(),
            Substitute.For<IFileSystemPicker>(), new QueueingDialog(), Substitute.For<INotificationService>(), store);
    }

    private static FleetApiClient CoordinatorThatAcceptsBuilds(Guid projectId)
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/packages", StringComparison.Ordinal))
            {
                var job = new DeploymentJob { ProjectId = projectId, Kind = JobKind.PackageBuild };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(job) };
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/package-projects", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<string>()) };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");
        return client;
    }

    /// <summary>
    /// Dialog double that presses the primary option, like a user clicking "Queue Build",
    /// and returns once the dialog is closed or dismissed.
    /// </summary>
    private sealed class QueueingDialog : IDialog
    {
        public async Task<bool> Show<TViewModel>(
            Maybe<TViewModel> viewModel,
            Maybe<IObservable<string>> title,
            Func<Maybe<TViewModel>, ICloseable, IEnumerable<IOption>> optionsFactory,
            Maybe<object> icon = default,
            DialogTone tone = DialogTone.Neutral,
            DialogSize size = DialogSize.Auto)
        {
            var closeable = new AwaitableCloseable();
            var primary = optionsFactory(viewModel, closeable).Single(option => option.Role == OptionRole.Primary);
            primary.Command.Execute(null);
            return await closeable.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private sealed class AwaitableCloseable : ICloseable
        {
            private readonly TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<bool> Closed => closed.Task;

            public void Close() => closed.TrySetResult(true);

            public void Dismiss() => closed.TrySetResult(false);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
