using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetDeployer.Fleet.App.ViewModels;

[Section(name: "Jobs", icon: "mdi-history", sortIndex: 1)]
public partial class BuildsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly IConnectedFleetClientContext clientContext;
    private readonly INavigator navigator;
    private readonly IFileSystemPicker fileSystemPicker;
    private readonly IDialog dialog;
    private readonly INotificationService notificationService;
    private readonly CompositeDisposable disposables = [];
    private readonly ConcurrentDictionary<Guid, byte[]?> projectIcons = new();
    private FleetApiClient? loadedClient;

    [Reactive] private bool isLoading;
    [Reactive] private string? error;

    public ObservableCollection<JobViewModel> Builds { get; } = [];
    public ListStatus BuildsStatus { get; }

    public BuildsViewModel(
        IConnectedFleetClientContext clientContext,
        INavigator navigator,
        IFileSystemPicker fileSystemPicker,
        IDialog dialog,
        INotificationService notificationService,
        IScheduler? autoRefreshScheduler = null)
    {
        this.clientContext = clientContext;
        this.navigator = navigator;
        this.fileSystemPicker = fileSystemPicker;
        this.dialog = dialog;
        this.notificationService = notificationService;
        BuildsStatus = ListStatus.Of(Builds, this.WhenAnyValue(vm => vm.IsLoading));
        disposables.Add(BuildsStatus);

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadBuilds);
        ClearFinishedJobsCommand = ReactiveCommand.CreateFromTask(ClearFinishedJobs);
        disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load jobs")));
        disposables.Add(ClearFinishedJobsCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot clear jobs")));
        disposables.Add(Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshBuildElapsed()));
        AutoRefresh = new AutoRefresh(RefreshInBackground, autoRefreshScheduler);
        disposables.Add(AutoRefresh);

        Header = Observable.Return<object>(new SectionHeader("Jobs",
            "Builds and deploys of all projects, newest first",
            new HeaderAction("Clear Jobs", "mdi-delete-sweep-outline", ClearFinishedJobsCommand),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public ReactiveCommand<Unit, Maybe<Result>> ClearFinishedJobsCommand { get; }
    public AutoRefresh AutoRefresh { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => disposables.Dispose();

    private void RefreshBuildElapsed()
    {
        foreach (var build in Builds)
            build.RefreshElapsed();
    }

    private async Task<Maybe<Result>> LoadBuilds()
    {
        IsLoading = true;
        Error = null;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                await LoadBuilds(client);
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

        var jobs = await SyncBuilds(client);
        await LoadProjectIconsAsync(client, jobs, reuseKnownIcons: true);
    }

    private async Task<Maybe<Result>> ClearFinishedJobs()
    {
        Error = null;
        return await clientContext.Require().Bind(async client =>
        {
            var current = await client.GetAllJobsAsync();
            if (!await dialog.ConfirmClearBuilds(current, "from all projects"))
                return;

            IsLoading = true;
            try
            {
                await client.DeleteFinishedJobsAsync();
                await LoadBuilds(client);
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

    private async Task LoadBuilds(FleetApiClient client)
    {
        var jobs = await SyncBuilds(client);
        loadedClient = client;
        IsLoading = false;
        await LoadProjectIconsAsync(client, jobs, reuseKnownIcons: false);
    }

    private async Task<IReadOnlyList<DeploymentJob>> SyncBuilds(FleetApiClient client)
    {
        var projectsTask = client.GetProjectsAsync();
        var jobsTask = client.GetAllJobsAsync();

        await Task.WhenAll(projectsTask, jobsTask);

        var projects = await projectsTask;
        var jobs = await jobsTask;
        var projectNames = projects.ToDictionary(project => project.Id, project => project.Name);

        // Show the builds right away. Icons can take long to resolve on the coordinator the
        // first time, so they are filled in afterwards instead of holding the whole list.
        // Continuations stay on the caller's (UI) context, so rows can be updated directly.
        ObservableCollectionSync.Sync(
            Builds,
            jobs.OrderByDescending(job => job.EnqueuedAt),
            job => job.Id,
            viewModel => viewModel.Job.Id,
            job => new JobViewModel(
                job,
                client,
                navigator,
                projectDetail: null,
                fileSystemPicker: fileSystemPicker,
                projectName: ResolveProjectName(job.ProjectId, projectNames),
                clientContext: clientContext,
                notificationService: notificationService),
            (viewModel, job) => viewModel.ApplyJobUpdate(job, ResolveProjectName(job.ProjectId, projectNames)));

        return jobs;
    }

    /// <summary>
    /// Fills in the project icons of the build rows. Background refreshes reuse the icons
    /// already fetched, so polling does not download every icon again on each tick.
    /// </summary>
    private async Task LoadProjectIconsAsync(FleetApiClient client, IEnumerable<DeploymentJob> jobs, bool reuseKnownIcons)
    {
        if (!reuseKnownIcons)
            projectIcons.Clear();

        await Task.WhenAll(jobs.Select(job => job.ProjectId).Distinct().Select(async projectId =>
        {
            if (!projectIcons.TryGetValue(projectId, out var icon))
            {
                icon = await TryLoadProjectIcon(client, projectId);
                projectIcons[projectId] = icon;
            }

            if (icon is null)
                return;

            foreach (var build in Builds.Where(build => build.Job.ProjectId == projectId))
                build.ProjectIconBytes = icon;
        }));
    }

    private static string ResolveProjectName(Guid projectId, IReadOnlyDictionary<Guid, string> projectNames)
    {
        return projectNames.TryGetValue(projectId, out var name)
            ? name
            : $"Project {projectId.ToString("N")[..8]}";
    }

    private static async Task<byte[]?> TryLoadProjectIcon(FleetApiClient client, Guid projectId)
    {
        try
        {
            return await client.GetProjectIcon(projectId);
        }
        catch
        {
            return null;
        }
    }
}
