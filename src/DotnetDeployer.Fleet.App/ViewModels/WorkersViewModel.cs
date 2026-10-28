using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetDeployer.Fleet.App.ViewModels;

[Section(name: "Workers", icon: "mdi-server", sortIndex: 2)]
public partial class WorkersViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly IConnectedFleetClientContext clientContext;
    private readonly IDialog _dialog;
    private readonly INavigator _navigator;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly INotificationService _notificationService;
    private readonly CompositeDisposable _disposables = [];
    private FleetApiClient? _loadedClient;

    [Reactive] private bool _isLoading;
    [Reactive] private string? _coordinatorVersion;

    public ObservableCollection<WorkerItemViewModel> Workers { get; } = [];
    public ListStatus WorkersStatus { get; }

    public WorkersViewModel(
        IConnectedFleetClientContext clientContext,
        IDialog dialog,
        INotificationService notificationService,
        IBackendHealthMonitor health,
        INavigator navigator,
        IFileSystemPicker fileSystemPicker,
        IScheduler? uiScheduler = null,
        IScheduler? autoRefreshScheduler = null)
    {
        this.clientContext = clientContext;
        _dialog = dialog;
        _navigator = navigator;
        _fileSystemPicker = fileSystemPicker;
        _notificationService = notificationService;
        _coordinatorVersion = health.Current?.Version;
        WorkersStatus = ListStatus.Of(Workers, this.WhenAnyValue(vm => vm.IsLoading));
        _disposables.Add(WorkersStatus);

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        RemoveWorkerCommand = ReactiveCommand.CreateFromTask<WorkerItemViewModel, Maybe<Result>>(RemoveAsync);
        OpenCurrentJobCommand = ReactiveCommand.CreateFromTask<WorkerItemViewModel, Maybe<Result>>(OpenCurrentJobAsync);
        _disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load workers")));
        _disposables.Add(RemoveWorkerCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot remove worker")));
        AutoRefresh = new AutoRefresh(RefreshInBackground, autoRefreshScheduler);
        _disposables.Add(AutoRefresh);
        _disposables.Add(OpenCurrentJobCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot open the build")));

        // Remember the last version the coordinator reported: a failed probe reports none, and
        // that must not hide the version marks of every worker until the next successful one.
        _disposables.Add(health.Snapshots
            .Select(snapshot => snapshot.Version)
            .Where(version => !string.IsNullOrEmpty(version))
            .DistinctUntilChanged()
            .ObserveOn(uiScheduler ?? RxSchedulers.MainThreadScheduler)
            .Subscribe(version => CoordinatorVersion = version));

        _disposables.Add(this.WhenAnyValue(vm => vm.CoordinatorVersion)
            .Subscribe(version =>
            {
                foreach (var worker in Workers)
                    worker.CoordinatorVersion = version;
            }));

        Header = Observable.Return<object>(new SectionHeader("Workers",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public ReactiveCommand<WorkerItemViewModel, Maybe<Result>> RemoveWorkerCommand { get; }
    public AutoRefresh AutoRefresh { get; }
    public ReactiveCommand<WorkerItemViewModel, Maybe<Result>> OpenCurrentJobCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task<Maybe<Result>> LoadAsync()
    {
        IsLoading = true;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                await SyncWorkers(client);
                _loadedClient = client;
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
        if (IsLoading || _loadedClient is not { } client)
            return;

        await SyncWorkers(client);
    }

    private async Task SyncWorkers(FleetApiClient client)
    {
        var workers = await client.GetWorkersAsync();
        ObservableCollectionSync.Sync(
            Workers,
            InDisplayOrder(workers),
            worker => worker.Id,
            viewModel => viewModel.Worker.Id,
            worker => new WorkerItemViewModel(worker, client, this, _notificationService) { CoordinatorVersion = CoordinatorVersion },
            (viewModel, worker) => viewModel.ApplyWorkerUpdate(worker));
    }

    /// <summary>
    /// Connected workers (Online or Busy) come first and disconnected ones last, each group by name.
    /// Busy and Online share a group so a worker does not jump around every time it takes a build.
    /// </summary>
    internal static IEnumerable<FleetApiClient.WorkerInfo> InDisplayOrder(IEnumerable<FleetApiClient.WorkerInfo> workers) =>
        workers
            .OrderBy(worker => worker.Status == Core.Domain.WorkerStatus.Offline ? 1 : 0)
            .ThenBy(worker => worker.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(worker => worker.Id);

    private async Task<Maybe<Result>> RemoveAsync(WorkerItemViewModel item)
    {
        if (!await _dialog.ConfirmDestructive("Remove worker?", RemovalMessage(item.Worker), "Remove"))
            return Maybe<Result>.None;

        return await clientContext.Require().Bind(async client =>
        {
            // The coordinator fails whatever the worker was running or had been assigned.
            await client.DeleteWorkerAsync(item.Worker.Id);
            Workers.Remove(item);
        });
    }

    private async Task<Maybe<Result>> OpenCurrentJobAsync(WorkerItemViewModel item)
    {
        if (item.Worker.CurrentJobId is not { } jobId)
            return Maybe<Result>.None;

        return await clientContext.Require().Bind(async client =>
        {
            var job = await client.GetJobAsync(jobId)
                      ?? throw new InvalidOperationException("The build no longer exists. Refresh the list.");

            var navigation = await _navigator.Go(() => new JobDetailViewModel(
                job,
                client,
                clientContext,
                _navigator,
                parentProject: null,
                fileSystemPicker: _fileSystemPicker,
                notificationService: _notificationService));
            if (navigation.IsFailure)
                throw new InvalidOperationException(navigation.Error);
        });
    }

    private static string RemovalMessage(FleetApiClient.WorkerInfo worker)
    {
        var message = $"Remove the worker \"{worker.Name}\" from the fleet? Builds it is running or has been assigned will be marked as failed.";
        return worker.Status == Core.Domain.WorkerStatus.Offline
            ? message
            : message + " It is still connected: it will stop receiving builds and has to be registered again to rejoin.";
    }
}

public partial class WorkerItemViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;

    public FleetApiClient.WorkerInfo Worker { get; private set; }

    [Reactive] private double _maxDiskUsageGb;
    [Reactive] private bool _isSaving;
    [Reactive] private bool _isSaved;
    [Reactive] private string? _coordinatorVersion;

    public WorkerItemViewModel(FleetApiClient.WorkerInfo worker, FleetApiClient client, WorkersViewModel parent, INotificationService notificationService)
    {
        Worker = worker;
        _client = client;
        _maxDiskUsageGb = worker.MaxDiskUsageGb;

        var canSave = this.WhenAnyValue(vm => vm.MaxDiskUsageGb, vm => vm.Worker, vm => vm.IsSaving,
            (value, current, saving) => !saving && HasDiskChange(value, current.MaxDiskUsageGb));
        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync, canSave);
        RemoveCommand = ReactiveCommand.CreateFromObservable(() => parent.RemoveWorkerCommand.Execute(this).Select(_ => Unit.Default));
        OpenCurrentJobCommand = ReactiveCommand.CreateFromObservable(
            () => parent.OpenCurrentJobCommand.Execute(this).Select(_ => Unit.Default),
            this.WhenAnyValue(vm => vm.Worker, current => current.CurrentJobId is not null));

        SaveConfigCommand.HandleErrorsWith(notificationService, Maybe.From("Cannot save worker settings"));

        // The "Saved" confirmation belongs to the saved value: editing it again clears it.
        this.WhenAnyValue(vm => vm.MaxDiskUsageGb)
            .Skip(1)
            .Subscribe(_ => IsSaved = false);

        this.WhenAnyValue(vm => vm.CoordinatorVersion)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(VersionDiffers));
                this.RaisePropertyChanged(nameof(VersionMismatchMessage));
            });
    }

    public ReactiveCommand<Unit, Result> SaveConfigCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCurrentJobCommand { get; }

    public void ApplyWorkerUpdate(FleetApiClient.WorkerInfo updated)
    {
        if (updated.Id != Worker.Id) return;

        var canSyncDiskValue = !IsSaving && !HasDiskChange(MaxDiskUsageGb, Worker.MaxDiskUsageGb);
        Worker = updated;
        if (canSyncDiskValue)
            MaxDiskUsageGb = updated.MaxDiskUsageGb;

        this.RaisePropertyChanged(nameof(Worker));
        this.RaisePropertyChanged(nameof(StatusLabel));
        this.RaisePropertyChanged(nameof(StatusIcon));
        this.RaisePropertyChanged(nameof(StatusTone));
        this.RaisePropertyChanged(nameof(EmbeddedLabel));
        this.RaisePropertyChanged(nameof(CapabilityLabel));
        this.RaisePropertyChanged(nameof(DisplayVersion));
        this.RaisePropertyChanged(nameof(VersionDiffers));
        this.RaisePropertyChanged(nameof(VersionMismatchMessage));
        this.RaisePropertyChanged(nameof(LastSeenText));
        this.RaisePropertyChanged(nameof(CurrentJobLabel));
    }

    public string StatusLabel => Worker.Status.ToString();

    public IIcon StatusIcon => new Icon(Worker.Status switch
    {
        Core.Domain.WorkerStatus.Online => "mdi-check-circle",
        Core.Domain.WorkerStatus.Busy => "mdi-progress-wrench",
        Core.Domain.WorkerStatus.Offline => "mdi-close-circle",
        _ => "mdi-help-circle-outline"
    });

    public Tone StatusTone => Worker.Status switch
    {
        Core.Domain.WorkerStatus.Online => Tone.Success,
        Core.Domain.WorkerStatus.Busy => Tone.Info,
        Core.Domain.WorkerStatus.Offline => Tone.Danger,
        _ => Tone.Neutral
    };

    public string EmbeddedLabel => Worker.IsEmbedded ? "(embedded)" : string.Empty;

    public string? DisplayVersion => VersionDisplay.VisibleWithPrefix(Worker.Version);

    /// <summary>True when the worker runs a different release than the coordinator.</summary>
    public bool VersionDiffers => VersionDisplay.Differ(Worker.Version, CoordinatorVersion);

    public string? VersionMismatchMessage => VersionDiffers
        ? $"This worker runs {DisplayVersion} but the coordinator runs {VersionDisplay.VisibleWithPrefix(CoordinatorVersion)}. Update the worker so they match."
        : null;

    public string LastSeenText => LocalTimestampFormatter.Format(Worker.LastSeenAt);

    /// <summary>What the worker is building right now, or null when it is not running a build.</summary>
    public string? CurrentJobLabel => Worker.CurrentJobId is null
        ? null
        : $"Building {Worker.CurrentJobProjectName ?? "a project"}";

    public string CapabilityLabel
    {
        get
        {
            if (Worker.ProcessorCount <= 0 && Worker.TotalMemoryMb <= 0)
                return string.Empty;

            var cores = Worker.ProcessorCount > 0 ? $"{Worker.ProcessorCount} core(s)" : null;
            var memory = Worker.TotalMemoryMb > 0
                ? $"{(Worker.TotalMemoryMb / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} GB"
                : null;
            var arch = !string.IsNullOrWhiteSpace(Worker.Architecture)
                ? Worker.Architecture
                : null;

            return string.Join(" · ", new[] { cores, memory, arch }.Where(s => s is not null));
        }
    }

    private static bool HasDiskChange(double value, double saved) => Math.Abs(value - saved) >= 0.001;

    private async Task<Result> SaveConfigAsync()
    {
        IsSaving = true;
        IsSaved = false;
        try
        {
            var value = MaxDiskUsageGb;
            var result = await Result.Try(() => _client.UpdateWorkerConfigAsync(Worker.Id, value));
            if (result.IsSuccess)
            {
                Worker = Worker with { MaxDiskUsageGb = value };
                this.RaisePropertyChanged(nameof(Worker));
                IsSaved = true;
            }

            return result;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
