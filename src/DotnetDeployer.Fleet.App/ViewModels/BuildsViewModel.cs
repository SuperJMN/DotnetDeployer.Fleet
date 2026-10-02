using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetDeployer.Fleet.App.ViewModels;

[Section(name: "Builds", icon: "mdi-history", sortIndex: 1)]
public partial class BuildsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly IConnectedFleetClientContext clientContext;
    private readonly INavigator navigator;
    private readonly IFileSystemPicker fileSystemPicker;
    private readonly INotificationService notificationService;
    private readonly CompositeDisposable disposables = [];

    [Reactive] private bool isLoading;
    [Reactive] private string? error;

    public ObservableCollection<JobViewModel> Builds { get; } = [];

    public BuildsViewModel(
        IConnectedFleetClientContext clientContext,
        INavigator navigator,
        IFileSystemPicker fileSystemPicker,
        INotificationService notificationService)
    {
        this.clientContext = clientContext;
        this.navigator = navigator;
        this.fileSystemPicker = fileSystemPicker;
        this.notificationService = notificationService;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadBuilds);
        ClearFinishedJobsCommand = ReactiveCommand.CreateFromTask(ClearFinishedJobs);
        disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load builds")));
        disposables.Add(ClearFinishedJobsCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot clear builds")));
        disposables.Add(Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshBuildElapsed()));

        Header = Observable.Return<object>(new SectionHeader("Builds",
            "All projects, newest first",
            new HeaderAction("Clear Builds", "mdi-broom", ClearFinishedJobsCommand),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public ReactiveCommand<Unit, Maybe<Result>> ClearFinishedJobsCommand { get; }
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

    private async Task<Maybe<Result>> ClearFinishedJobs()
    {
        IsLoading = true;
        Error = null;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                await client.DeleteFinishedJobsAsync();
                await LoadBuilds(client);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadBuilds(FleetApiClient client)
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

        IsLoading = false;
        await LoadProjectIconsAsync(client, jobs);
    }

    private async Task LoadProjectIconsAsync(FleetApiClient client, IEnumerable<DeploymentJob> jobs)
    {
        await Task.WhenAll(jobs.Select(job => job.ProjectId).Distinct().Select(async projectId =>
        {
            var icon = await TryLoadProjectIcon(client, projectId);
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
