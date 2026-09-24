using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Commands;
using Zafiro.UI.Navigation;
using DialogOption = Zafiro.Avalonia.Dialogs.Option;

namespace DotnetDeployer.Fleet.App.ViewModels;

public partial class ProjectDetailViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly IConnectedFleetClientContext clientContext;
    private readonly INavigator _navigator;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly IDialog _dialog;
    private readonly INotificationService notificationService;
    private readonly CompositeDisposable _disposables = [];
    private readonly BehaviorSubject<object> _header;

    public Project Project { get; private set; }
    public ProjectSecretsViewModel ProjectSecrets { get; }
    public PackageBuildOptionsViewModel BuildOptions { get; }

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;

    public ObservableCollection<JobViewModel> Jobs { get; } = [];
    public bool HasJobHistory => Jobs.Count > 0;
    public bool ShowEmptyJobHistory => !IsLoading && Jobs.Count == 0;

    public ProjectDetailViewModel(
        Project project,
        FleetApiClient client,
        IConnectedFleetClientContext clientContext,
        INavigator navigator,
        IFileSystemPicker fileSystemPicker,
        IDialog dialog,
        INotificationService notificationService)
    {
        Project = project;
        _client = client;
        this.clientContext = clientContext;
        _navigator = navigator;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;
        this.notificationService = notificationService;
        ProjectSecrets = new ProjectSecretsViewModel(project.Id, client, clientContext, notificationService);
        BuildOptions = new PackageBuildOptionsViewModel(project.Id, clientContext);

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadJobs);
        DeployCommand = ReactiveCommand.CreateFromTask(Deploy);
        BuildPackagesCommand = ReactiveCommand.CreateFromTask(ShowBuildPackagesDialog);
        ClearFinishedJobsCommand = ReactiveCommand.CreateFromTask(ClearFinishedJobs);
        EditCommand = ReactiveCommand.Create(() =>
        {
            _navigator.Go(() => new EditProjectViewModel(Project, _client, _navigator, projectsForRefresh: null));
        });

        _disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load project")));
        _disposables.Add(DeployCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot queue deploy")));
        _disposables.Add(BuildPackagesCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot queue build")));
        _disposables.Add(ClearFinishedJobsCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot clear builds")));
        _disposables.Add(Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshJobElapsed()));
        _header = new BehaviorSubject<object>(CreateHeader());
        Header = _header.AsObservable();
    }

    private SectionHeader CreateHeader() =>
        new(
            Project.Name,
            $"{Project.GitUrl} @ {Project.Branch}",
            new HeaderAction("Queue Deploy", "mdi-rocket-launch", DeployCommand, isPrimary: true),
            new HeaderAction("Queue Build", "mdi-package-variant-closed", BuildPackagesCommand, isPrimary: true),
            new HeaderAction("Clear Builds", "mdi-broom", ClearFinishedJobsCommand),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand),
            new HeaderAction("Project Secrets", "mdi-dots-horizontal", ProjectSecrets),
            new HeaderAction("Edit", "mdi-pencil", EditCommand));

    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public ReactiveCommand<Unit, Maybe<Result>> DeployCommand { get; }
    public ReactiveCommand<Unit, Maybe<Result>> BuildPackagesCommand { get; }
    public ReactiveCommand<Unit, Maybe<Result>> ClearFinishedJobsCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose()
    {
        _disposables.Dispose();
        ProjectSecrets.Dispose();
        _header.Dispose();
    }

    private async Task<Maybe<Result>> LoadJobs()
    {
        IsLoading = true;
        RaiseJobHistoryStateChanged();
        Error = null;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                var projectTask = client.GetProjectAsync(Project.Id);
                var jobsTask = client.GetProjectJobsAsync(Project.Id);

                await Task.WhenAll(projectTask, jobsTask);

                var project = await projectTask;
                if (project is not null)
                    ApplyProjectUpdate(project);

                var jobs = await jobsTask;
                ObservableCollectionSync.Sync(
                    Jobs,
                    jobs.OrderByDescending(j => j.EnqueuedAt),
                    job => job.Id,
                    viewModel => viewModel.Job.Id,
                    job => new JobViewModel(job, client, _navigator, this, _fileSystemPicker, clientContext: clientContext, notificationService: notificationService),
                    (viewModel, job) => viewModel.ApplyJobUpdate(job));

                RaiseJobHistoryStateChanged();
            });
        }
        finally
        {
            IsLoading = false;
            RaiseJobHistoryStateChanged();
        }
    }

    private async Task<Maybe<Result>> Deploy()
    {
        Error = null;
        return await clientContext.Require().Bind(async client =>
        {
            var job = await client.EnqueueDeployAsync(Project.Id);
            Jobs.Insert(0, new JobViewModel(job, client, _navigator, this, _fileSystemPicker, clientContext: clientContext, notificationService: notificationService));
            RaiseJobHistoryStateChanged();
        });
    }

    private async Task<Maybe<Result>> ShowBuildPackagesDialog()
    {
        _ = BuildOptions.LoadPackageProjects();

        ICloseable? closeDialog = null;
        var result = Maybe.From(Result.Success());
        var queueCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var request = BuildOptions.CreateRequest();
            if (request is null) return;

            result = await QueuePackageBuild(request);
            if (result.HasNoValue || result.Value.IsSuccess)
                closeDialog?.Close();
        });
        queueCommand.ThrownExceptions.Subscribe(ex => BuildOptions.Error = ex.Message);

        await _dialog.Show(BuildOptions, "Queue build", (_, closeable) =>
        {
            closeDialog = closeable;
            return new IOption[]
            {
                new DialogOption("Cancel",
                    ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                    new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                new DialogOption("Queue Build",
                    queueCommand.Enhance(),
                    new Settings { IsDefault = true, Role = OptionRole.Primary }),
            };
        }, size: DialogSize.Wide);

        return result;
    }

    private async Task<Maybe<Result>> QueuePackageBuild(PackageBuildRequest request)
    {
        Error = null;
        return await clientContext.Require().Bind(async client =>
        {
            var job = await client.EnqueuePackageBuildAsync(Project.Id, request);
            Jobs.Insert(0, new JobViewModel(job, client, _navigator, this, _fileSystemPicker, clientContext: clientContext, notificationService: notificationService));
            RaiseJobHistoryStateChanged();
        });
    }

    private async Task<Maybe<Result>> ClearFinishedJobs()
    {
        return await clientContext.Require().Bind(async client =>
        {
            await client.DeleteFinishedProjectJobsAsync(Project.Id);
            var jobs = await client.GetProjectJobsAsync(Project.Id);
            ObservableCollectionSync.Sync(
                Jobs,
                jobs.OrderByDescending(j => j.EnqueuedAt),
                job => job.Id,
                viewModel => viewModel.Job.Id,
                job => new JobViewModel(job, client, _navigator, this, _fileSystemPicker, clientContext: clientContext, notificationService: notificationService),
                (viewModel, job) => viewModel.ApplyJobUpdate(job));

            RaiseJobHistoryStateChanged();
        });
    }

    private void ApplyProjectUpdate(Project updated)
    {
        if (updated.Id != Project.Id) return;

        Project = updated;
        this.RaisePropertyChanged(nameof(Project));
        _header.OnNext(CreateHeader());
    }

    private void RaiseJobHistoryStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasJobHistory));
        this.RaisePropertyChanged(nameof(ShowEmptyJobHistory));
    }

    private void RefreshJobElapsed()
    {
        foreach (var job in Jobs)
            job.RefreshElapsed();
    }
}

public partial class PackageBuildOptionsViewModel : ReactiveObject
{
    private readonly Guid _projectId;
    private readonly IConnectedFleetClientContext clientContext;

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;
    [Reactive] private string _packageProject = "";

    public PackageBuildOptionsViewModel(Guid projectId, IConnectedFleetClientContext clientContext)
    {
        _projectId = projectId;
        this.clientContext = clientContext;

        foreach (var platform in PackagePlatformViewModel.CreateDefaults())
            PackagePlatforms.Add(platform);

        foreach (var target in PackagePlatforms.SelectMany(platform => platform.Targets))
        {
            target.WhenAnyValue(x => x.IsSelected)
                .Subscribe(_ =>
                {
                    Error = null;
                    this.RaisePropertyChanged(nameof(SelectedPackageTargetCount));
                });
        }
    }

    public ObservableCollection<PackagePlatformViewModel> PackagePlatforms { get; } = [];
    public ObservableCollection<string> PackageProjects { get; } = [];
    public bool HasPackageProjects => PackageProjects.Count > 0;
    public int SelectedPackageTargetCount => PackagePlatforms.Sum(p => p.Targets.Count(t => t.IsSelected));

    public async Task LoadPackageProjects()
    {
        IsLoading = true;
        try
        {
            var projectResult = await clientContext.Require().Bind(client => client.GetPackageProjectsAsync(_projectId));
            if (projectResult.HasNoValue || projectResult.Value.IsFailure)
                return;

            var projects = projectResult.Value.Value;
            PackageProjects.Clear();
            foreach (var project in projects)
                PackageProjects.Add(project);

            if (PackageProjects.Count == 1 && string.IsNullOrWhiteSpace(PackageProject))
                PackageProject = PackageProjects[0];

            this.RaisePropertyChanged(nameof(HasPackageProjects));
        }
        catch
        {
            // Package project discovery is advisory; users can still type the project manually.
        }
        finally
        {
            IsLoading = false;
        }
    }

    public PackageBuildRequest? CreateRequest()
    {
        Error = null;
        if (PackageProjects.Count > 1 && string.IsNullOrWhiteSpace(PackageProject))
        {
            Error = "Select the package project from deployer.yaml.";
            return null;
        }

        var targets = PackagePlatforms
            .SelectMany(p => p.Targets)
            .Where(t => t.IsSelected)
            .Select(t => new PackageBuildTarget
            {
                Format = t.Format,
                Architecture = t.Architecture
            })
            .ToList();

        if (targets.Count == 0)
        {
            Error = "Select at least one package target.";
            return null;
        }

        return new PackageBuildRequest
        {
            PackageProject = string.IsNullOrWhiteSpace(PackageProject) ? null : PackageProject.Trim(),
            Targets = targets
        };
    }
}

public partial class PackagePlatformViewModel : ReactiveObject
{
    public PackagePlatformViewModel(string name, IEnumerable<PackageFormatViewModel> formats)
    {
        Name = name;
        Formats = new ObservableCollection<PackageFormatViewModel>(formats);
    }

    public string Name { get; }
    public ObservableCollection<PackageFormatViewModel> Formats { get; }
    public IEnumerable<PackageTargetOptionViewModel> Targets => Formats.SelectMany(format => format.Architectures);

    public static IEnumerable<PackagePlatformViewModel> CreateDefaults()
    {
        yield return new PackagePlatformViewModel("Windows",
        [
            new("Setup (.exe)", "exe-setup",
            [
                new("Setup (.exe)", "exe-setup", "X64", "x64", isSelected: true),
                new("Setup (.exe)", "exe-setup", "X86", "x86")
            ]),
            new("Self-extracting EXE", "exe-sfx",
            [
                new("Self-extracting EXE", "exe-sfx", "X64", "x64"),
                new("Self-extracting EXE", "exe-sfx", "X86", "x86")
            ]),
            new("MSIX", "msix",
            [
                new("MSIX", "msix", "X64", "x64"),
                new("MSIX", "msix", "X86", "x86")
            ])
        ]);

        yield return new PackagePlatformViewModel("Linux",
        [
            new("DEB", "deb",
            [
                new("DEB", "deb", "X64", "x64"),
                new("DEB", "deb", "ARM64", "arm64")
            ]),
            new("RPM", "rpm",
            [
                new("RPM", "rpm", "X64", "x64"),
                new("RPM", "rpm", "ARM64", "arm64")
            ]),
            new("AppImage", "appimage",
            [
                new("AppImage", "appimage", "X64", "x64")
            ]),
            new("Flatpak", "flatpak",
            [
                new("Flatpak", "flatpak", "X64", "x64")
            ])
        ]);

        yield return new PackagePlatformViewModel("macOS",
        [
            new("DMG", "dmg",
            [
                new("DMG", "dmg", "X64", "x64"),
                new("DMG", "dmg", "ARM64", "arm64")
            ])
        ]);

        yield return new PackagePlatformViewModel("Android",
        [
            new("APK", "apk",
            [
                new("APK", "apk", "X64", "x64")
            ]),
            new("AAB", "aab",
            [
                new("AAB", "aab", "X64", "x64")
            ])
        ]);
    }
}

public class PackageFormatViewModel
{
    public PackageFormatViewModel(
        string label,
        string format,
        IEnumerable<PackageTargetOptionViewModel> architectures)
    {
        Label = label;
        Format = format;
        Architectures = new ObservableCollection<PackageTargetOptionViewModel>(architectures);
    }

    public string Label { get; }
    public string Format { get; }
    public ObservableCollection<PackageTargetOptionViewModel> Architectures { get; }
}

public partial class PackageTargetOptionViewModel : ReactiveObject
{
    public PackageTargetOptionViewModel(
        string formatLabel,
        string format,
        string architectureLabel,
        string architecture,
        bool isSelected = false)
    {
        FormatLabel = formatLabel;
        Format = format;
        ArchitectureLabel = architectureLabel;
        Architecture = architecture;
        _isSelected = isSelected;
    }

    public string FormatLabel { get; }
    public string Format { get; }
    public string ArchitectureLabel { get; }
    public string Architecture { get; }

    [Reactive] private bool _isSelected;
}

public partial class JobViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly IConnectedFleetClientContext? clientContext;
    private readonly INavigator _navigator;
    private readonly ProjectDetailViewModel? _projectDetail;
    private readonly IFileSystemPicker? _fileSystemPicker;
    private readonly INotificationService? notificationService;

    public DeploymentJob Job { get; private set; }
    private string? projectName;
    public string? ProjectName
    {
        get => projectName;
        private set => this.RaiseAndSetIfChanged(ref projectName, value);
    }
    public bool HasProjectName => !string.IsNullOrWhiteSpace(ProjectName);

    private string? _version;
    private string _displayName = string.Empty;
    private byte[]? projectIconBytes;
    private bool hasProjectIcon;
    private bool hasNoProjectIcon = true;

    public JobViewModel(
        DeploymentJob job,
        FleetApiClient client,
        INavigator navigator,
        ProjectDetailViewModel? projectDetail = null,
        IFileSystemPicker? fileSystemPicker = null,
        string? projectName = null,
        IConnectedFleetClientContext? clientContext = null,
        INotificationService? notificationService = null)
    {
        Job = job;
        _client = client;
        this.clientContext = clientContext;
        _navigator = navigator;
        _projectDetail = projectDetail;
        _fileSystemPicker = fileSystemPicker;
        this.notificationService = notificationService;
        this.projectName = projectName;
        _version = job.Version;
        _displayName = ComputeDisplayName(job.Version);

        OpenCommand = ReactiveCommand.Create(Open);
    }

    /// <summary>The detected GitVersion-style version, or null if none was reported yet.</summary>
    public string? Version
    {
        get => _version;
        private set
        {
            if (_version == value) return;
            this.RaiseAndSetIfChanged(ref _version, value);
            DisplayName = ComputeDisplayName(value);
        }
    }

    /// <summary>
    /// Friendly label shown in deployment lists. Falls back to a short prefix of the
    /// job GUID until a version is detected from the worker's log stream.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        private set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    public byte[]? ProjectIconBytes
    {
        get => projectIconBytes;
        set
        {
            var normalized = value is { Length: > 0 } ? value : null;
            this.RaiseAndSetIfChanged(ref projectIconBytes, normalized);
            HasProjectIcon = normalized is not null;
            HasNoProjectIcon = normalized is null;
        }
    }

    public bool HasProjectIcon
    {
        get => hasProjectIcon;
        private set => this.RaiseAndSetIfChanged(ref hasProjectIcon, value);
    }

    public bool HasNoProjectIcon
    {
        get => hasNoProjectIcon;
        private set => this.RaiseAndSetIfChanged(ref hasNoProjectIcon, value);
    }

    public string ElapsedText => JobDurationFormatter.Format(Job.GetElapsedDurationMs(DateTimeOffset.UtcNow));

    /// <summary>
    /// Pushes a fresher snapshot of the underlying <see cref="DeploymentJob"/> into this VM,
    /// raising change notifications for the user-visible fields. Used when the deployment is
    /// renamed (a version was detected) or any other field is updated by the coordinator.
    /// </summary>
    public void ApplyJobUpdate(DeploymentJob updated, string? projectName = null)
    {
        if (updated.Id != Job.Id) return;

        Job = updated;
        if (projectName is not null)
            ProjectName = projectName;

        Version = updated.Version;
        this.RaisePropertyChanged(nameof(Job));
        this.RaisePropertyChanged(nameof(HasProjectName));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusIcon));
        this.RaisePropertyChanged(nameof(StatusBrush));
        this.RaisePropertyChanged(nameof(KindText));
        RefreshElapsed();
    }

    public void RefreshElapsed() => this.RaisePropertyChanged(nameof(ElapsedText));

    private string ComputeDisplayName(string? version) =>
        string.IsNullOrWhiteSpace(version) ? Job.Id.ToString("N")[..8] : VersionDisplay.Visible(version)!;

    public string StatusText => Job.Status switch
    {
        JobStatus.Queued => "Queued",
        JobStatus.Assigned => "Assigned",
        JobStatus.Running => "Running",
        JobStatus.Succeeded => "Succeeded",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        JobStatus.AwaitingNuGetIndex => "Waiting for NuGet",
        _ => Job.Status.ToString()
    };

    public string KindText => Job.Kind == JobKind.PackageBuild ? "Packages" : "Deploy";

    public IIcon StatusIcon => new Icon(Job.Status switch
    {
        JobStatus.Queued => "mdi-clock-outline",
        JobStatus.Assigned => "mdi-account-arrow-right-outline",
        JobStatus.Running => "mdi-rocket-launch",
        JobStatus.Succeeded => "mdi-check-circle",
        JobStatus.Failed => "mdi-alert-circle",
        JobStatus.Cancelled => "mdi-cancel",
        JobStatus.AwaitingNuGetIndex => "mdi-clock-outline",
        _ => "mdi-help-circle-outline"
    });

    public IBrush StatusBrush => Job.Status switch
    {
        JobStatus.Queued => new SolidColorBrush(Color.Parse("#9E9E9E")),
        JobStatus.Assigned => new SolidColorBrush(Color.Parse("#42A5F5")),
        JobStatus.Running => new SolidColorBrush(Color.Parse("#29B6F6")),
        JobStatus.Succeeded => new SolidColorBrush(Color.Parse("#43A047")),
        JobStatus.Failed => new SolidColorBrush(Color.Parse("#E53935")),
        JobStatus.Cancelled => new SolidColorBrush(Color.Parse("#FB8C00")),
        JobStatus.AwaitingNuGetIndex => new SolidColorBrush(Color.Parse("#29B6F6")),
        _ => Brushes.Gray
    };

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenCommand { get; }

    private void Open()
    {
        if (clientContext is null || notificationService is null)
            return;

        _navigator.Go(() => new JobDetailViewModel(
            Job,
            _client,
            clientContext,
            _navigator,
            _projectDetail,
            _fileSystemPicker,
            notificationService));
    }
}
