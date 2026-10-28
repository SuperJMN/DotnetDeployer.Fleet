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

public sealed class ProjectFormDialogTests
{
    static ProjectFormDialogTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task Editing_from_the_project_page_uses_the_same_dialog_as_adding()
    {
        var api = new RecordingApi();
        var dialog = new UserDialog(
            UserDialog.Edit<EditProjectViewModel>(form => form.Branch = "develop"),
            UserDialog.Press(OptionRole.Primary));
        using var detail = ProjectDetail(AProject(), api, dialog);

        await detail.EditCommand.Execute();

        dialog.Titles.Should().Equal("Edit Project");
        api.Updates.Should().ContainSingle().Which.Should().Contain("\"branch\":\"develop\"");
    }

    [Fact]
    public async Task Editing_from_the_project_list_opens_the_form_in_a_dialog()
    {
        var api = new RecordingApi();
        var dialog = new UserDialog(UserDialog.Dismiss);
        var project = new ProjectViewModel(AProject(), api.Client, Substitute.For<INavigator>(), null!,
            Substitute.For<IFileSystemPicker>(), dialog);

        await project.EditCommand.Execute();

        dialog.Titles.Should().Equal("Edit Project");
        dialog.ViewModels.Should().ContainSingle().Which.Should().BeOfType<EditProjectViewModel>();
    }

    [Fact]
    public async Task Leaving_a_form_without_changes_does_not_ask()
    {
        var dialog = new UserDialog(UserDialog.Dismiss);

        var saved = await dialog.ShowProjectForm(new EditProjectViewModel(AProject(), AClient()), "Edit Project");

        saved.Should().BeFalse();
        dialog.Titles.Should().Equal("Edit Project");
    }

    [Fact]
    public async Task Leaving_a_form_with_unsaved_changes_asks_before_discarding_them()
    {
        var api = new RecordingApi();
        var dialog = new UserDialog(
            UserDialog.Edit<EditProjectViewModel>(form => form.Name = "Renamed"),
            UserDialog.Dismiss,
            UserDialog.Press(OptionRole.Destructive));

        var saved = await dialog.ShowProjectForm(new EditProjectViewModel(AProject(), api.Client), "Edit Project");

        saved.Should().BeFalse();
        dialog.Titles.Should().Equal("Edit Project", "Discard changes?");
        api.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Keeping_on_editing_returns_to_the_form_with_the_changes_still_there()
    {
        var api = new RecordingApi();
        var form = new EditProjectViewModel(AProject(), api.Client);
        var dialog = new UserDialog(
            UserDialog.Edit<EditProjectViewModel>(form => form.Name = "Renamed"),
            UserDialog.Dismiss,
            UserDialog.Press(OptionRole.Cancel),
            UserDialog.Press(OptionRole.Primary));

        var saved = await dialog.ShowProjectForm(form, "Edit Project");

        saved.Should().BeTrue();
        dialog.Titles.Should().Equal("Edit Project", "Discard changes?", "Edit Project");
        dialog.ViewModels[2].Should().BeSameAs(form);
        api.Updates.Should().ContainSingle().Which.Should().Contain("\"name\":\"Renamed\"");
    }

    [Fact]
    public async Task Cancelling_a_new_project_with_typed_data_asks_before_discarding_it()
    {
        var dialog = new UserDialog(
            UserDialog.Edit<AddProjectViewModel>(form => form.Name = "App"),
            UserDialog.Press(OptionRole.Cancel),
            UserDialog.Press(OptionRole.Destructive));

        var saved = await dialog.ShowProjectForm(new AddProjectViewModel(AClient()), "Add Project");

        saved.Should().BeFalse();
        dialog.Titles.Should().Equal("Add Project", "Discard changes?");
    }

    [Fact]
    public async Task Closing_the_dialog_while_a_save_runs_waits_for_it_instead_of_asking_to_discard()
    {
        var api = new RecordingApi(holdUpdates: true);
        var cancelWhileSaving = true;
        var dialog = new UserDialog(
            UserDialog.Edit<EditProjectViewModel>(form => form.Name = "Renamed"),
            UserDialog.Start(OptionRole.Primary),
            new UserDialog.Step(shown => cancelWhileSaving = shown.Options.First(o => o.Role == OptionRole.Cancel).Command.CanExecute(null), ClosesDialog: false),
            UserDialog.Dismiss);

        var showing = dialog.ShowProjectForm(new EditProjectViewModel(AProject(), api.Client), "Edit Project");
        api.ReleaseUpdates();
        var saved = await showing.WaitAsync(TimeSpan.FromSeconds(30));

        saved.Should().BeTrue();
        cancelWhileSaving.Should().BeFalse();
        dialog.Titles.Should().Equal("Edit Project");
        api.Updates.Should().ContainSingle().Which.Should().Contain("\"name\":\"Renamed\"");
    }

    private static Project AProject() =>
        new() { Id = Guid.NewGuid(), Name = "App", GitUrl = "https://example.test/repo.git", Branch = "master" };

    private static FleetApiClient AClient() => new(new HttpClientHandler(), new HttpClientHandler());

    private static ProjectDetailViewModel ProjectDetail(Project project, RecordingApi api, IDialog dialog)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(api.Client))));
        return new ProjectDetailViewModel(project, api.Client, context, Substitute.For<INavigator>(),
            Substitute.For<IFileSystemPicker>(), dialog, Substitute.For<INotificationService>());
    }

    /// <summary>
    /// Plays a user through dialogs, one step at a time. Editing the form keeps the dialog open
    /// for the next step; pressing an option or dismissing waits until the dialog has closed.
    /// </summary>
    private sealed class UserDialog(params UserDialog.Step[] steps) : IDialog
    {
        private readonly Queue<Step> pending = new(steps);

        public List<string> Titles { get; } = [];
        public List<object?> ViewModels { get; } = [];

        public static Step Dismiss { get; } = new(shown => shown.Closeable.Dismiss(), ClosesDialog: true);

        public static Step Press(OptionRole role) =>
            new(shown => shown.Options.First(option => option.Role == role).Command.Execute(null), ClosesDialog: true);

        public static Step Start(OptionRole role) =>
            new(shown => shown.Options.First(option => option.Role == role).Command.Execute(null), ClosesDialog: false);

        public static Step Edit<TForm>(Action<TForm> edit) =>
            new(shown => edit((TForm)shown.ViewModel!), ClosesDialog: false);

        public async Task<bool> Show<TViewModel>(
            Maybe<TViewModel> viewModel,
            Maybe<IObservable<string>> title,
            Func<Maybe<TViewModel>, ICloseable, IEnumerable<IOption>> optionsFactory,
            Maybe<object> icon = default,
            DialogTone tone = DialogTone.Neutral,
            DialogSize size = DialogSize.Auto)
        {
            Titles.Add(title.HasValue ? await title.Value.FirstAsync() : "");
            ViewModels.Add(viewModel.GetValueOrDefault());

            var closeable = new Closeable();
            var shown = new ShownDialog(viewModel.GetValueOrDefault(), optionsFactory(viewModel, closeable).ToList(), closeable);
            Step step;
            do
            {
                pending.Should().NotBeEmpty("the dialog \"{0}\" is still open", Titles[^1]);
                step = pending.Dequeue();
                step.Act(shown);
            } while (!step.ClosesDialog);

            return await closeable.Result.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public sealed record Step(Action<ShownDialog> Act, bool ClosesDialog);

        public sealed record ShownDialog(object? ViewModel, IReadOnlyList<IOption> Options, Closeable Closeable);

        public sealed class Closeable : ICloseable
        {
            private readonly TaskCompletionSource<bool> result = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<bool> Result => result.Task;

            public void Close() => result.TrySetResult(true);

            public void Dismiss() => result.TrySetResult(false);
        }
    }

    private sealed class RecordingApi
    {
        private readonly TaskCompletionSource updatesReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingApi(bool holdUpdates = false)
        {
            if (!holdUpdates)
                updatesReleased.SetResult();

            var handler = new StubHandler(async request =>
            {
                if (request.Method == HttpMethod.Put)
                {
                    await updatesReleased.Task;
                    Updates.Add(await request.Content!.ReadAsStringAsync());
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<DeploymentJob>()) };

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            Client = new FleetApiClient(handler, handler);
            Client.SetBaseAddress("http://localhost:5000");
        }

        public FleetApiClient Client { get; }
        public List<string> Updates { get; } = [];

        public void ReleaseUpdates() => updatesReleased.TrySetResult();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request);
    }
}
