using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.Avalonia.Dialogs;
using Zafiro.DivineBytes;
using Zafiro.UI;
using Zafiro.UI.Commands;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;
using DialogOption = Zafiro.Avalonia.Dialogs.Option;

namespace DotnetDeployer.Fleet.App.ViewModels;

[Section(name: "Projects", icon: "mdi-folder-outline", sortIndex: 0)]
public partial class ProjectsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly IConnectedFleetClientContext clientContext;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly IDialog _dialog;
    private readonly INotificationService notificationService;
    private readonly CompositeDisposable _disposables = [];
    internal readonly INavigator Navigator;

    [Reactive] private ProjectViewModel? _selectedProject;
    [Reactive] private bool _isLoading;

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    public ProjectsViewModel(
        IConnectedFleetClientContext clientContext,
        INavigator navigator,
        IFileSystemPicker fileSystemPicker,
        IDialog dialog,
        INotificationService notificationService)
    {
        this.clientContext = clientContext;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;
        this.notificationService = notificationService;
        Navigator = navigator;

        var refresh = ReactiveCommand.CreateFromTask(LoadProjects);
        _disposables.Add(refresh.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load projects")));
        RefreshCommand = refresh.Enhance("Refresh");

        var addProject = ReactiveCommand.CreateFromTask(OpenAddProject);
        _disposables.Add(addProject.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot add project")));
        AddProjectCommand = addProject.Enhance("Add Project");

        Header = Observable.Return<object>(new SectionHeader("Projects",
            new HeaderAction("Add Project", "mdi-plus", AddProjectCommand, isPrimary: true),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    internal IConnectedFleetClientContext ClientContext => clientContext;
    internal INotificationService NotificationService => notificationService;
    public IEnhancedCommand<Maybe<Result>> RefreshCommand { get; }
    public IEnhancedCommand<Maybe<Result>> AddProjectCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task<Maybe<Result>> LoadProjects()
    {
        IsLoading = true;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                var list = await client.GetProjectsAsync();
                ApplyProjects(client, list);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<Maybe<Result>> OpenAddProject()
    {
        return await clientContext.Require().Bind(async client =>
        {
            var vm = new AddProjectViewModel(client);

            var created = await _dialog.Show(vm, "Add Project", (_, closeable) =>
            {
                var saveCommand = ReactiveCommand.CreateFromTask(async () =>
                {
                    if (await vm.TrySaveAsync())
                    {
                        closeable.Close();
                    }
                }, vm.CanSave).Enhance();

                return new IOption[]
                {
                    new DialogOption("Cancel",
                        ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                        new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                    new DialogOption("Save",
                        saveCommand,
                        new Settings { IsDefault = true, Role = OptionRole.Primary }),
                };
            });

            if (created)
            {
                var list = await client.GetProjectsAsync();
                ApplyProjects(client, list);
            }
        });
    }

    private void ApplyProjects(FleetApiClient client, IEnumerable<Project> list)
    {
        ObservableCollectionSync.Sync(
            Projects,
            list,
            project => project.Id,
            viewModel => viewModel.Project.Id,
            project => new ProjectViewModel(project, client, Navigator, this, _fileSystemPicker, _dialog),
            (viewModel, project) => viewModel.ApplyProjectUpdate(project));

        foreach (var project in Projects)
            _ = project.LoadProjectIcon();
    }
}

public partial class ProjectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectsViewModel _parent;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly IDialog _dialog;
    private Guid? loadedIconProjectId;

    public Project Project { get; private set; }
    [Reactive] private byte[]? _projectIconBytes;
    [Reactive] private bool _hasProjectIcon;
    [Reactive] private bool _hasNoProjectIcon = true;
    [Reactive] private bool _isIconLoading;

    public ProjectViewModel(
        Project project,
        FleetApiClient client,
        INavigator navigator,
        ProjectsViewModel parent,
        IFileSystemPicker fileSystemPicker,
        IDialog dialog)
    {
        Project = project;
        _client = client;
        _navigator = navigator;
        _parent = parent;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;

        OpenCommand = ReactiveCommand.Create(Open);
        EditCommand = ReactiveCommand.Create(Edit);
        ChangeIconCommand = ReactiveCommand.CreateFromTask(ChangeProjectIcon);
        ResetIconCommand = ReactiveCommand.CreateFromTask(ResetProjectIcon);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeIconCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetIconCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public void ApplyProjectUpdate(Project updated)
    {
        if (updated.Id != Project.Id) return;

        var iconInvalidated =
            !string.Equals(Project.GitUrl, updated.GitUrl, StringComparison.Ordinal)
            || !string.Equals(Project.Branch, updated.Branch, StringComparison.Ordinal)
            || !string.Equals(Project.GitToken, updated.GitToken, StringComparison.Ordinal);

        Project = updated;
        this.RaisePropertyChanged(nameof(Project));

        if (iconInvalidated)
        {
            loadedIconProjectId = null;
            SetProjectIcon(null);
        }
    }

    public async Task LoadProjectIcon()
    {
        if (IsIconLoading || loadedIconProjectId == Project.Id)
            return;

        var projectId = Project.Id;
        IsIconLoading = true;

        try
        {
            var bytes = await _client.GetProjectIcon(projectId);
            if (Project.Id != projectId)
                return;

            SetProjectIcon(bytes);
            loadedIconProjectId = projectId;
        }
        catch
        {
            if (Project.Id == projectId)
                SetProjectIcon(null);
        }
        finally
        {
            if (Project.Id == projectId)
                IsIconLoading = false;
        }
    }

    public async Task ChangeProjectIcon()
    {
        var pickResult = await _fileSystemPicker.PickForOpen(
            new FileTypeFilter("Image files", ["*.png", "*.jpg", "*.jpeg", "*.ico"]),
            new FileTypeFilter("All files", ["*"]));

        if (pickResult.IsFailure || pickResult.Value.HasNoValue)
            return;

        var file = pickResult.Value.Value;
        await using var stream = file.ToStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();

        await _client.SetProjectIcon(Project.Id, bytes, file.Name);
        loadedIconProjectId = Project.Id;
        SetProjectIcon(bytes);
    }

    public async Task ResetProjectIcon()
    {
        await _client.ResetProjectIcon(Project.Id);
        loadedIconProjectId = null;
        SetProjectIcon(null);
        await LoadProjectIcon();
    }

    private void Open()
    {
        _navigator.Go(() => new ProjectDetailViewModel(
            Project,
            _client,
            _parent.ClientContext,
            _navigator,
            _fileSystemPicker,
            _dialog,
            _parent.NotificationService));
    }

    private void Edit()
    {
        _navigator.Go(() => new EditProjectViewModel(Project, _client, _navigator, _parent));
    }

    private async Task DeleteAsync()
    {
        var confirmed = await _dialog.ConfirmDestructive(
            "Delete project?",
            $"Delete the project \"{Project.Name}\"? This cannot be undone.",
            "Delete");
        if (!confirmed)
            return;

        await _client.DeleteProjectAsync(Project.Id);
        _parent.RefreshCommand.Execute(Unit.Default).Subscribe();
    }

    private void SetProjectIcon(byte[]? bytes)
    {
        ProjectIconBytes = bytes is { Length: > 0 } ? bytes : null;
        HasProjectIcon = ProjectIconBytes is not null;
        HasNoProjectIcon = ProjectIconBytes is null;
    }
}
