using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DynamicData;
using DynamicData.Binding;
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
    private readonly IPackageTargetSelectionStore packageTargetSelections;
    private readonly TimeProvider timeProvider;
    private readonly CompositeDisposable _disposables = [];
    private readonly ObservableCollection<ProjectViewModel> allProjects = [];
    private readonly ReadOnlyObservableCollection<ProjectViewModel> projects;
    private FleetApiClient? loadedClient;
    internal readonly INavigator Navigator;

    [Reactive] private ProjectViewModel? _selectedProject;
    [Reactive] private bool _isLoading;
    [Reactive] private string? _searchText;
    [Reactive] private ProjectSortOption _selectedSort;
    [Reactive] private bool _hasNoMatches;
    [Reactive] private bool _hasVisibleProjects;

    /// <summary>The projects that match the search, in the chosen order.</summary>
    public ReadOnlyObservableCollection<ProjectViewModel> Projects => projects;

    /// <summary>Loading and empty states of the whole project list, regardless of the search.</summary>
    public ListStatus ProjectsStatus { get; }

    public IReadOnlyList<ProjectSortOption> SortOptions => ProjectSortOption.All;

    public ProjectsViewModel(
        IConnectedFleetClientContext clientContext,
        INavigator navigator,
        IFileSystemPicker fileSystemPicker,
        IDialog dialog,
        INotificationService notificationService,
        IPackageTargetSelectionStore packageTargetSelections,
        ISettingsService settings,
        IScheduler? autoRefreshScheduler = null,
        TimeProvider? timeProvider = null)
    {
        this.clientContext = clientContext;
        this.packageTargetSelections = packageTargetSelections;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;
        this.notificationService = notificationService;
        Navigator = navigator;
        _selectedSort = ProjectSortOption.For(settings.GetProjectSortOrder());
        ProjectsStatus = ListStatus.Of(allProjects, this.WhenAnyValue(vm => vm.IsLoading));
        _disposables.Add(ProjectsStatus);

        // TableView neither sorts nor filters, so the list is shaped here. Rows are re-placed
        // only when a value the list searches or orders by changes, and then only moved: rows
        // update their own cells, and a Refresh reaching the binding would remove and re-insert
        // the row, which makes the table rebuild it and drop the selection. The filter takes the
        // search as state because the predicate-observable overload turns a Refresh into a Replace.
        var comparer = this.WhenAnyValue(vm => vm.SelectedSort)
            .Select(option => ProjectOrdering.For(option.Order));
        _disposables.Add(allProjects.ToObservableChangeSet()
            .AutoRefreshOnObservable(project => project.PlacementChanges)
            .Filter(this.WhenAnyValue(vm => vm.SearchText), (text, project) => project.Matches(text))
            .Sort(comparer)
            .WhereReasonsAreNot(ListChangeReason.Refresh)
            .Bind(out projects)
            .Subscribe());

        _disposables.Add(Observable.Merge(
                CollectionChanges.Of(allProjects),
                CollectionChanges.Of(projects),
                this.WhenAnyValue(vm => vm.SearchText).Select(_ => Unit.Default))
            .Subscribe(_ =>
            {
                HasVisibleProjects = projects.Count > 0;
                HasNoMatches = allProjects.Count > 0 && projects.Count == 0;
            }));

        _disposables.Add(this.WhenAnyValue(vm => vm.SelectedSort)
            .Skip(1)
            .Subscribe(option => settings.SetProjectSortOrder(option.Order)));

        var refresh = ReactiveCommand.CreateFromTask(LoadProjects);
        _disposables.Add(refresh.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load projects")));
        RefreshCommand = refresh.Enhance("Refresh");

        var addProject = ReactiveCommand.CreateFromTask(OpenAddProject);
        _disposables.Add(addProject.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot add project")));
        AddProjectCommand = addProject.Enhance("Add Project");

        AutoRefresh = new AutoRefresh(RefreshInBackground, autoRefreshScheduler);
        _disposables.Add(AutoRefresh);

        Header = Observable.Return<object>(new SectionHeader("Projects",
            new HeaderAction("Add Project", "mdi-plus", AddProjectCommand, isPrimary: true),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    internal IConnectedFleetClientContext ClientContext => clientContext;
    internal INotificationService NotificationService => notificationService;
    internal IPackageTargetSelectionStore PackageTargetSelections => packageTargetSelections;
    public IEnhancedCommand<Maybe<Result>> RefreshCommand { get; }
    public IEnhancedCommand<Maybe<Result>> AddProjectCommand { get; }
    public AutoRefresh AutoRefresh { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task<Maybe<Result>> LoadProjects()
    {
        IsLoading = true;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                await SyncProjects(client);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshInBackground()
    {
        // Only once a load has connected; a background tick must never prompt for a connection.
        if (IsLoading || loadedClient is not { } client)
            return;

        await SyncProjects(client);
    }

    private async Task<Maybe<Result>> OpenAddProject()
    {
        return await clientContext.Require().Bind(async client =>
        {
            var vm = new AddProjectViewModel(client);

            var created = await _dialog.ShowProjectForm(vm, "Add Project");

            if (created)
                await SyncProjects(client);
        });
    }

    private async Task SyncProjects(FleetApiClient client)
    {
        var projectsTask = client.GetProjectsAsync();
        var jobsTask = client.GetAllJobsAsync();
        await Task.WhenAll(projectsTask, jobsTask);

        ApplyProjects(client, await projectsTask, await jobsTask);
        loadedClient = client;
    }

    private void ApplyProjects(FleetApiClient client, IEnumerable<Project> list, IReadOnlyCollection<DeploymentJob> jobs)
    {
        ObservableCollectionSync.Sync(
            allProjects,
            list,
            project => project.Id,
            viewModel => viewModel.Project.Id,
            project => new ProjectViewModel(project, client, Navigator, this, _fileSystemPicker, _dialog, notificationService, timeProvider),
            (viewModel, project) => viewModel.ApplyProjectUpdate(project));

        var activeJobs = jobs
            .Where(job => JobStatusDisplay.IsActive(job.Status))
            .ToLookup(job => job.ProjectId);

        foreach (var project in allProjects)
        {
            project.ApplyActiveJob(activeJobs[project.Project.Id]);
            _ = project.LoadProjectIcon();
        }
    }

}

/// <summary>The orders of the project list. Ties, and projects without a date, go by name.</summary>
internal static class ProjectOrdering
{
    private static readonly IComparer<ProjectViewModel> ByName = Comparer<ProjectViewModel>.Create((left, right) =>
    {
        var byName = string.Compare(left.Project.Name, right.Project.Name, StringComparison.CurrentCultureIgnoreCase);
        return byName != 0 ? byName : left.Project.Id.CompareTo(right.Project.Id);
    });

    public static IComparer<ProjectViewModel> For(ProjectSortOrder order) => order switch
    {
        ProjectSortOrder.LastDeploy => NewestFirst(project => project.Project.LastDeployFinishedAt),
        ProjectSortOrder.RecentActivity => NewestFirst(project => project.RecentActivityAt),
        _ => ByName
    };

    private static IComparer<ProjectViewModel> NewestFirst(Func<ProjectViewModel, DateTimeOffset?> date) =>
        Comparer<ProjectViewModel>.Create((left, right) => (date(left), date(right)) switch
        {
            (null, null) => ByName.Compare(left, right),
            (null, _) => 1,
            (_, null) => -1,
            var (l, r) when l == r => ByName.Compare(left, right),
            var (l, r) => r!.Value.CompareTo(l!.Value)
        });
}

public partial class ProjectViewModel : ReactiveObject, IOpenable
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectsViewModel _parent;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly IDialog _dialog;
    private readonly TimeProvider timeProvider;
    private Guid? loadedIconProjectId;
    private DeploymentJob? activeJob;

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
        IDialog dialog,
        INotificationService notificationService,
        TimeProvider? timeProvider = null)
    {
        Project = project;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        _client = client;
        _navigator = navigator;
        _parent = parent;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;

        OpenCommand = ReactiveCommand.Create(Open);
        EditCommand = ReactiveCommand.CreateFromTask(EditAsync);
        ChangeIconCommand = ReactiveCommand.CreateFromTask(ChangeProjectIcon);
        ResetIconCommand = ReactiveCommand.CreateFromTask(ResetProjectIcon);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);

        ChangeIconCommand.ThrownExceptions.NotifyWith(notificationService, "Cannot change project icon");
        ResetIconCommand.ThrownExceptions.NotifyWith(notificationService, "Cannot reset project icon");
        DeleteCommand.ThrownExceptions.NotifyWith(notificationService, "Cannot delete project");
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeIconCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetIconCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    System.Windows.Input.ICommand IOpenable.OpenCommand => OpenCommand;

    /// <summary>How the project looks for new commits: "Manual" when polling is off, otherwise its interval.</summary>
    public string PollingText => Project.PollingIntervalMinutes > 0
        ? $"Every {Project.PollingIntervalMinutes} min"
        : "Manual";

    /// <summary>
    /// When the last deploy finished and what it published, such as "2 h ago · 0.1.20",
    /// or "Never deployed".
    /// </summary>
    public string LastDeployText
    {
        get
        {
            if (Project.LastDeployFinishedAt is not { } finishedAt)
                return "Never deployed";

            var when = RelativeTimeFormatter.Format(finishedAt, timeProvider.GetUtcNow());
            return VersionDisplay.Visible(Project.LastDeployVersion) is { Length: > 0 } version
                ? $"{when} · {version}"
                : when;
        }
    }

    /// <summary>The local date and time of the last deploy, for its tooltip.</summary>
    public string LastDeployTimestampText => LocalTimestampFormatter.Format(Project.LastDeployFinishedAt);

    public bool HasLastDeployStatus => Project.LastDeployStatus is not null;

    public string LastDeployStatusText => Project.LastDeployStatus is { } status
        ? JobStatusDisplay.Text(status, Project.LastDeployNuGetVerification)
        : "--";

    public IIcon? LastDeployStatusIcon => Project.LastDeployStatus is { } status
        ? new Icon(JobStatusDisplay.IconCode(status, Project.LastDeployNuGetVerification))
        : null;

    public Tone LastDeployStatusTone => Project.LastDeployStatus is { } status
        ? JobStatusDisplay.Tone(status)
        : Tone.Neutral;

    public string LastDeployCommitText => JobNaming.ShortSha(Project.LastDeployCommitSha) ?? "--";

    /// <summary>
    /// Signals when a value the project list searches or orders by changes: the name, the
    /// repository, the last deploy or the recent activity. Refreshes with the same data stay quiet.
    /// </summary>
    internal IObservable<Unit> PlacementChanges =>
        this.WhenAnyValue(vm => vm.Project, vm => vm.ActiveJob,
                (_, _) => (Project.Name, Project.GitUrl, Project.LastDeployFinishedAt, RecentActivityAt))
            .DistinctUntilChanged()
            .Skip(1)
            .Select(_ => Unit.Default);

    /// <summary>The job of this project that is queued or running right now, if any.</summary>
    public DeploymentJob? ActiveJob
    {
        get => activeJob;
        private set => this.RaiseAndSetIfChanged(ref activeJob, value);
    }

    public bool HasActiveJob => ActiveJob is not null;

    /// <summary>What the project is doing right now, such as "Running · Deploy".</summary>
    public string? ActiveJobText => ActiveJob is { } job
        ? $"{JobStatusDisplay.Text(job.Status)} · {(job.Kind == JobKind.PackageBuild ? "Packages" : "Deploy")}"
        : null;

    public IIcon? ActiveJobIcon => ActiveJob is { } job ? new Icon(JobStatusDisplay.IconCode(job.Status)) : null;

    public Tone ActiveJobTone => ActiveJob is { } job ? JobStatusDisplay.Tone(job.Status) : Tone.Neutral;

    /// <summary>
    /// The latest moment any job of the project did something: when the last one finished, or
    /// when the one in progress started (or was queued), whichever is later.
    /// </summary>
    public DateTimeOffset? RecentActivityAt
    {
        get
        {
            var activeSince = ActiveJob is { } job ? job.StartedAt ?? job.EnqueuedAt : (DateTimeOffset?)null;
            return (Project.LastActivityAt, activeSince) switch
            {
                (null, var active) => active,
                (var finished, null) => finished,
                var (finished, active) => finished > active ? finished : active
            };
        }
    }

    /// <summary>True when the search text appears in the project's name or repository. Case is ignored.</summary>
    public bool Matches(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        var text = searchText.Trim();
        return Project.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
               || Project.GitUrl.Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// Shows the job the project is busy with: a running one before a queued one, and the
    /// newest when several are in the same state. None leaves the project idle.
    /// </summary>
    public void ApplyActiveJob(IEnumerable<DeploymentJob> activeJobs)
    {
        var job = activeJobs
            .Where(candidate => candidate.ProjectId == Project.Id && JobStatusDisplay.IsActive(candidate.Status))
            .OrderByDescending(candidate => candidate.Status is JobStatus.Running or JobStatus.AwaitingNuGetIndex)
            .ThenByDescending(candidate => candidate.Status == JobStatus.Assigned)
            .ThenByDescending(candidate => candidate.EnqueuedAt)
            .FirstOrDefault();

        if (job?.Id == ActiveJob?.Id && job?.Status == ActiveJob?.Status)
            return;

        ActiveJob = job;
        this.RaisePropertyChanged(nameof(HasActiveJob));
        this.RaisePropertyChanged(nameof(ActiveJobText));
        this.RaisePropertyChanged(nameof(ActiveJobIcon));
        this.RaisePropertyChanged(nameof(ActiveJobTone));
        this.RaisePropertyChanged(nameof(RecentActivityAt));
    }

    public void ApplyProjectUpdate(Project updated)
    {
        if (updated.Id != Project.Id) return;

        var iconInvalidated =
            !string.Equals(Project.GitUrl, updated.GitUrl, StringComparison.Ordinal)
            || !string.Equals(Project.Branch, updated.Branch, StringComparison.Ordinal)
            || !string.Equals(Project.GitToken, updated.GitToken, StringComparison.Ordinal);

        Project = updated;
        this.RaisePropertyChanged(nameof(Project));
        this.RaisePropertyChanged(nameof(PollingText));
        this.RaisePropertyChanged(nameof(LastDeployText));
        this.RaisePropertyChanged(nameof(LastDeployTimestampText));
        this.RaisePropertyChanged(nameof(HasLastDeployStatus));
        this.RaisePropertyChanged(nameof(LastDeployStatusText));
        this.RaisePropertyChanged(nameof(LastDeployStatusIcon));
        this.RaisePropertyChanged(nameof(LastDeployStatusTone));
        this.RaisePropertyChanged(nameof(LastDeployCommitText));
        this.RaisePropertyChanged(nameof(RecentActivityAt));

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
            _parent.NotificationService,
            _parent.PackageTargetSelections));
    }

    private async Task EditAsync()
    {
        if (await _dialog.ShowProjectForm(new EditProjectViewModel(Project, _client), "Edit Project"))
            _parent.RefreshCommand.Execute(Unit.Default).Subscribe();
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
