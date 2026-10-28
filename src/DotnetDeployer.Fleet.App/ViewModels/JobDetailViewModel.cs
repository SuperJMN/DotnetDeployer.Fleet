using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AvaloniaTerminal;
using CSharpFunctionalExtensions;
using DynamicData;
using DynamicData.Binding;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Logging;
using DotnetDeployer.Fleet.App.Views.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.DivineBytes;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using IOPath = System.IO.Path;

namespace DotnetDeployer.Fleet.App.ViewModels;

public partial class JobDetailViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly IConnectedFleetClientContext clientContext;
    private readonly INotificationService notificationService;
    private readonly INavigator _navigator;
    private readonly IFileSystemPicker? _fileSystemPicker;
    private readonly ITextClipboard _clipboard;
    private readonly ProjectDetailViewModel? _parentProject;
    private readonly CompositeDisposable disposables = [];
    private readonly SourceList<LogLine> _logs = new();
    private readonly JobPhaseTree _phaseTree = new(FormatPhaseName);
    private readonly IDisposable _filterSubscription;
    private readonly BehaviorSubject<object> _header;
    private LogTerminalFeed? _terminalFeed;
    private TerminalControlModel? _terminalModel;
    private SectionHeader currentHeader;
    private bool isDisposed;
    private bool revealFirstErrorPending;
    private bool terminalRedrawn;
    private Guid? resolvedWorkerId;
    private bool phasesLoaded;

    public DeploymentJob Job { get; private set; }

    [Reactive] private string _statusText = string.Empty;
    [Reactive] private IIcon _statusIcon = new Icon();
    [Reactive] private Tone _statusTone;
    [Reactive] private bool _isStreaming;
    [Reactive] private bool _canCancel;
    [Reactive] private string? _errorMessage;
    [Reactive] private LogSeverity _minSeverity = LogSeverity.None;
    [Reactive] private string _searchText = string.Empty;
    [Reactive] private string _searchResultText = string.Empty;
    [Reactive] private string? _currentPhase;
    [Reactive] private DateTimeOffset? _currentPhaseStartedAt;
    [Reactive] private bool _isDetailedLogVisible;
    [Reactive] private string? _workerName;
    [Reactive] private string _copyResultText = string.Empty;

    public ReadOnlyObservableCollection<JobPhaseRowContainer> Phases => _phaseTree.Phases;
    public ObservableCollection<PackageArtifactViewModel> Artifacts { get; } = new();

    public bool HasPhases => Phases.Count > 0;
    /// <summary>The phases have not been fetched yet, so their absence says nothing.</summary>
    public bool IsLoadingPhases => !phasesLoaded;
    public bool HasNoPhaseEvents => phasesLoaded && !HasPhases;
    public bool HasArtifacts => Artifacts.Count > 0;
    public bool HasCurrentPhase => !string.IsNullOrEmpty(_currentPhase);
    public string CurrentPhaseDisplay => string.IsNullOrEmpty(_currentPhase)
        ? string.Empty
        : FormatPhaseName(_currentPhase!);
    public string DetailedLogButtonText => IsDetailedLogVisible ? "Hide detailed log" : "Detailed log";

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);
    public bool IsNuGetVerificationPending => Job.Status == JobStatus.Succeeded && Job.NuGetVerification == NuGetVerificationStatus.Pending;
    public bool IsNuGetVerified => Job.Status == JobStatus.Succeeded && Job.NuGetVerification == NuGetVerificationStatus.Verified;
    public string ElapsedText => JobDurationFormatter.Format(Job.GetElapsedDurationMs(DateTimeOffset.UtcNow));
    public string? ShortCommitSha => JobNaming.ShortSha(Job.TriggerCommitSha);

    public ReadOnlyObservableCollection<LogLine> FilteredLogs { get; }

    /// <summary>
    /// The terminal that shows the visible log lines. It is replaced by an empty
    /// one whenever the lines must be redrawn, because escape sequences cannot
    /// remove what the terminal keeps in its scrollback.
    /// </summary>
    public TerminalControlModel? TerminalModel
    {
        get => _terminalModel;
        private set => this.RaiseAndSetIfChanged(ref _terminalModel, value);
    }

    public IReadOnlyList<SeverityFilterOption> SeverityFilters => SeverityFilterOption.All;

    private CancellationTokenSource? _cts;

    public JobDetailViewModel(
        DeploymentJob job,
        FleetApiClient client,
        IConnectedFleetClientContext clientContext,
        INavigator navigator,
        ProjectDetailViewModel? parentProject = null,
        IFileSystemPicker? fileSystemPicker = null,
        INotificationService notificationService = null!,
        ITextClipboard? clipboard = null)
    {
        Job = job;
        _client = client;
        this.clientContext = clientContext;
        this.notificationService = notificationService;
        _navigator = navigator;
        _fileSystemPicker = fileSystemPicker;
        _clipboard = clipboard ?? new AvaloniaTextClipboard();
        _parentProject = parentProject;
        ShowStatus(job.Status);
        _errorMessage = job.ErrorMessage;
        _canCancel = IsJobCancelable(job);

        CopyLogsCommand = ReactiveCommand.CreateFromTask(CopyLogsToClipboard);
        RefreshArtifactsCommand = ReactiveCommand.CreateFromTask(() => RefreshArtifactsAsync(default));
        NextSearchResultCommand = ReactiveCommand.Create(NavigateNextSearchResult);
        ToggleDetailedLogCommand = ReactiveCommand.Create(ToggleDetailedLog);
        ShowErrorInLogCommand = ReactiveCommand.Create(ShowErrorInLog);
        CancelJobCommand = ReactiveCommand.CreateFromTask(CancelJobAsync,
            this.WhenAnyValue(x => x.CanCancel));
        RefreshArtifactsCommand.ThrownExceptions.Subscribe(ex => ErrorMessage = ex.Message).DisposeWith(disposables);
        CopyLogsCommand.ThrownExceptions.NotifyWith(this.notificationService, "Cannot copy logs").DisposeWith(disposables);
        // The confirmation is a passing hint, not something to dismiss.
        this.WhenAnyValue(x => x.CopyResultText)
            .Where(text => text.Length > 0)
            .Throttle(TimeSpan.FromSeconds(4), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => CopyResultText = string.Empty)
            .DisposeWith(disposables);
        RefreshCommand = ReactiveCommand.CreateFromTask(() => RefreshSnapshot(default));
        RefreshCommand.Results().HandleErrorsWith(this.notificationService, Maybe.From("Cannot refresh job")).DisposeWith(disposables);
        currentHeader = CreateHeader();
        _header = new BehaviorSubject<object>(currentHeader);
        Header = _header.AsObservable();
        Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshElapsed())
            .DisposeWith(disposables);
        // NuGet verification finishes after the job; pick up its outcome without a manual refresh.
        Observable.Interval(TimeSpan.FromSeconds(30), RxSchedulers.MainThreadScheduler)
            .Where(_ => IsNuGetVerificationPending)
            .SelectMany(_ => Observable.FromAsync(() => _client.GetJobAsync(Job.Id)).Catch(Observable.Return<DeploymentJob?>(null)))
            .Where(updated => updated is not null)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(updated => ApplyJobSnapshot(updated!))
            .DisposeWith(disposables);
        ResolveWorkerName();

        var minSeverityChanges = this.WhenAnyValue(x => x.MinSeverity);

        _filterSubscription = _logs.Connect()
            .Filter(minSeverityChanges.Select(min => (Func<LogLine, bool>)(line => (int)line.Severity >= (int)min)))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Bind(out var filtered)
            // One terminal update per change set, so a filter change that removes
            // or inserts many lines redraws the terminal only once.
            .Subscribe(_ => SyncTerminal());

        FilteredLogs = filtered;

        // Drive terminal search from SearchText changes
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSearchTextChanged)
            .DisposeWith(disposables);

        if (job.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Assigned or JobStatus.AwaitingNuGetIndex)
        {
            StartStreaming();
        }
        else
        {
            LoadLogsAsync().ConfigureAwait(false);
        }
    }

    public IObservable<object> Header { get; }

    private async Task<Maybe<Result>> RefreshSnapshot(CancellationToken ct)
    {
        try
        {
            return await LoadSnapshot();
        }
        finally
        {
            // Even a failed load ends the wait, so the panel never keeps saying it is loading.
            phasesLoaded = true;
            RaisePhaseStateChanged();
        }
    }

    private async Task<Maybe<Result>> LoadSnapshot()
    {
        return await clientContext.Require().Bind(async client =>
        {
            var phasesTask = client.GetJobPhasesAsync(Job.Id);
            var jobTask = client.GetJobAsync(Job.Id);
            var artifactsTask = Job.Kind == JobKind.PackageBuild
                ? client.GetJobArtifactsAsync(Job.Id)
                : Task.FromResult<List<PackageArtifact>>([]);

            await Task.WhenAll(phasesTask, jobTask, artifactsTask);

            var phases = await phasesTask;
            var job = await jobTask;
            var artifacts = await artifactsTask;

            // Continuations stay on the caller's (UI) context, so the view model can be updated directly.
            if (job is not null)
                ApplyJobSnapshot(job);

            _phaseTree.Refresh(phases);
            ApplyArtifacts(artifacts);
            this.RaisePropertyChanged(nameof(HasCurrentPhase));
            this.RaisePropertyChanged(nameof(CurrentPhaseDisplay));
        });
    }

    private void RaisePhaseStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasPhases));
        this.RaisePropertyChanged(nameof(IsLoadingPhases));
        this.RaisePropertyChanged(nameof(HasNoPhaseEvents));
    }

    /// <summary>
    /// Maps a phase id (e.g. <c>package.generate.deb.x64</c>) to a human label
    /// (e.g. "Package · deb · x64"). Keeps the UI readable without forcing
    /// the backend to localise.
    /// </summary>
    internal static string FormatPhaseName(string name)
    {
        return name switch
        {
            "worker.git.clone" => "Cloning repository",
            "worker.solution.build" => "Building solution",
            "worker.solution.test" => "Running solution tests",
            "worker.deployer.invoke" => "Running DotnetDeployer",
            "worker.deployer.github" => "Publishing GitHub release",
            "worker.deployer.pack" => "Preparing release packages",
            "worker.inventory.verify" => "Verifying package inventory",
            "worker.nuget.push" => "Publishing NuGet packages",
            "worker.artifacts.upload" => "Uploading package artifacts",
            "version.resolve" => "Resolving version",
            "workload.restore" => "Restoring workloads",
            "android.prereqs" => "Installing Android prerequisites",
            "nuget.deploy" => "Publishing to NuGet",
            "nuget.pack" => "Packing NuGet",
            "nuget.push" => "Pushing NuGet",
            "github.deploy" => "Publishing to GitHub Releases",
            "github.pages.deploy" => "Publishing to GitHub Pages",
            "github.release.create" => "Creating GitHub release",
            "github.release.upload" => "Uploading release asset",
            _ when name.StartsWith("package.generate.") =>
                "Packaging · " + name["package.generate.".Length..].Replace('.', ' '),
            _ => name
        };
    }

    public ReactiveCommand<Unit, Unit> CopyLogsCommand { get; }
    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshArtifactsCommand { get; }
    public ReactiveCommand<Unit, Unit> NextSearchResultCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDetailedLogCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowErrorInLogCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelJobCommand { get; }

    /// <summary>
    /// Shows the visible log lines in <paramref name="model"/>. The terminal is
    /// written only from <see cref="FilteredLogs"/>, so every line appears once
    /// and the severity filter is respected while the job streams.
    /// </summary>
    public void SetTerminalModel(TerminalControlModel model)
    {
        TerminalModel = model;
        ShowLogsIn(text => TerminalModel?.Feed(text), ClearTerminal);
    }

    /// <summary>
    /// Writes the visible log lines again into an empty terminal with the current size.
    /// The terminal does not reflow text written at another width, so the view calls this
    /// whenever the number of columns changes (for example when the hidden log is first shown).
    /// </summary>
    public void RedrawTerminal()
    {
        if (TerminalModel is null) return;

        ReplaceTerminalModelWithEmptyOne();
        ShowLogsIn(text => TerminalModel?.Feed(text), ClearTerminal);
    }

    /// <summary>
    /// Writes the visible log lines, and every later change to them, to a terminal.
    /// </summary>
    /// <param name="writeToTerminal">Writes text to the terminal.</param>
    /// <param name="clearTerminal">Empties the terminal completely, scrollback included.</param>
    internal void ShowLogsIn(Action<string> writeToTerminal, Action clearTerminal)
    {
        _terminalFeed = new LogTerminalFeed(FilteredLogs, writeToTerminal, clearTerminal);
        terminalRedrawn = true;
        SyncTerminal();
    }

    public void DetachTerminalModel()
    {
        _terminalFeed = null;
        TerminalModel = null;
    }

    /// <summary>
    /// Brings the terminal up to date. A pending "show error" request is applied again
    /// after a redraw, because a redrawn terminal starts at the end of the log.
    /// </summary>
    private void SyncTerminal()
    {
        if (_terminalFeed is null) return;

        _terminalFeed.Sync();
        if (!terminalRedrawn) return;

        terminalRedrawn = false;
        if (revealFirstErrorPending)
            RevealFirstError();
    }

    private void ClearTerminal()
    {
        ReplaceTerminalModelWithEmptyOne();
        terminalRedrawn = true;
    }

    private void ReplaceTerminalModelWithEmptyOne()
    {
        if (TerminalModel is not { } current) return;

        var terminal = current.Terminal;
        TerminalModel = new TerminalControlModel(new TerminalOptions
        {
            Cols = terminal.Cols,
            Rows = terminal.Rows,
            Scrollback = terminal.Options.Scrollback,
            TabStopWidth = terminal.Options.TabStopWidth,
            TermName = terminal.Options.TermName,
            ConvertEol = terminal.Options.ConvertEol,
        });
    }

    private void OnSearchTextChanged(string? text)
    {
        if (!string.IsNullOrEmpty(text))
            revealFirstErrorPending = false;

        if (TerminalModel is null) return;

        if (string.IsNullOrEmpty(text))
        {
            SearchResultText = $"{FilteredLogs.Count} lines";
            return;
        }

        var count = TerminalModel.Search(text);
        SearchResultText = count > 0 ? $"{count} matches" : "No matches";
        if (count > 0)
            TerminalModel.SelectNextSearchResult();
    }

    private void NavigateNextSearchResult()
    {
        TerminalModel?.SelectNextSearchResult();
    }

    private void ToggleDetailedLog()
    {
        SetDetailedLogVisible(!IsDetailedLogVisible);
        if (!IsDetailedLogVisible)
            revealFirstErrorPending = false;
    }

    private void ShowErrorInLog()
    {
        revealFirstErrorPending = true;
        SetDetailedLogVisible(true);
        RevealFirstError();
    }

    private void SetDetailedLogVisible(bool isVisible)
    {
        if (IsDetailedLogVisible == isVisible)
            return;

        IsDetailedLogVisible = isVisible;
        this.RaisePropertyChanged(nameof(DetailedLogButtonText));
        PublishHeader();
    }

    /// <summary>
    /// Scrolls the terminal to the first error line, or to the end of the log when no line
    /// looks like an error (the cause of a failure is usually printed last).
    /// </summary>
    private void RevealFirstError()
    {
        if (TerminalModel is not { } terminal)
            return;

        var target = FirstErrorLocator.Find(FilteredLogs);
        var found = target.HasValue && SelectSearchMatch(terminal, target.Value);
        if (!found)
        {
            terminal.ClearSelection();
            terminal.EnsureCaretIsVisible();
        }

        SearchResultText = found ? "First error" : "End of log";
    }

    private static bool SelectSearchMatch(TerminalControlModel terminal, LogSearchTarget target)
    {
        var count = terminal.Search(target.Text);
        if (count == 0)
            return false;

        for (var i = 0; i < Math.Min(target.MatchIndex, count - 1); i++)
            terminal.SelectNextSearchResult();

        return true;
    }

    /// <summary>
    /// Copies every log line received so far, whatever the severity filter shows,
    /// and confirms how many lines were copied.
    /// </summary>
    private async Task CopyLogsToClipboard()
    {
        var lines = _logs.Items.Select(l => l.Text).ToList();
        var copied = await _clipboard.SetText(string.Join(Environment.NewLine, lines));
        if (copied.IsFailure)
            throw new InvalidOperationException(copied.Error);

        // Cleared first so copying the same lines again shows the confirmation anew.
        CopyResultText = string.Empty;
        CopyResultText = lines.Count == 1 ? "Copied 1 line" : $"Copied {lines.Count} lines";
    }

    private async Task CancelJobAsync()
    {
        try
        {
            await _client.CancelJobAsync(Job.Id);
            CanCancel = false;
            ShowStatus(JobStatus.Cancelled);
            PublishHeader();
        }
        catch (Exception ex)
        {
            StatusText = $"Cancel failed: {ex.Message}";
            PublishHeader();
        }
    }

    private async Task LoadLogsAsync()
    {
        try
        {
            await StreamLogsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            await notificationService.Show(ex.Message, Maybe.From("Cannot load logs"));
        }
    }

    private void StartStreaming()
    {
        _cts = new CancellationTokenSource();
        IsStreaming = true;
        PublishHeader();
        var cts = _cts;
        _ = StreamLogsAsync(cts.Token).ContinueWith(streaming => RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            IsStreaming = false;
            CanCancel = false;
            PublishHeader();

            // Leaving the page cancels the stream on purpose; anything else is a real failure.
            if (streaming.IsFaulted && !cts.IsCancellationRequested && !isDisposed)
            {
                var message = streaming.Exception!.GetBaseException().Message;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = notificationService.Show(message, Maybe.From("Log stream interrupted")));
            }
        }));
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        const int maxBatchSize = 200;
        var buffer = new List<LogLine>(maxBatchSize);
        var lastFlush = Environment.TickCount64;

        await foreach (var evt in _client.StreamJobEventsAsync(Job.Id, ct))
        {
            if (evt.Type == FleetApiClient.SseEventType.Status)
            {
                if (Enum.TryParse<JobStatus>(evt.Data, out var status))
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ShowStatus(status);
                        PublishHeader();
                    });
                continue;
            }

            buffer.Add(LogLine.FromText(evt.Data));

            var elapsed = Environment.TickCount64 - lastFlush;
            if (buffer.Count < maxBatchSize && elapsed < 100)
                continue;

            var batch = buffer.ToList();
            buffer.Clear();
            lastFlush = Environment.TickCount64;

            RxSchedulers.MainThreadScheduler.Schedule(() => _logs.AddRange(batch));
        }

        if (buffer.Count > 0)
        {
            var remaining = buffer.ToList();
            RxSchedulers.MainThreadScheduler.Schedule(() => _logs.AddRange(remaining));
        }

        var updated = await _client.GetJobAsync(Job.Id);
        if (updated is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ApplyJobSnapshot(updated);
            });

        await RefreshArtifactsAsync(default);
    }

    private async Task RefreshArtifactsAsync(CancellationToken ct)
    {
        if (Job.Kind != JobKind.PackageBuild)
            return;

        var artifacts = await _client.GetJobArtifactsAsync(Job.Id);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyArtifacts(artifacts));
    }

    private void ApplyJobSnapshot(DeploymentJob updated)
    {
        if (updated.Id != Job.Id) return;

        Job = updated;
        ShowStatus(updated.Status);
        ErrorMessage = updated.ErrorMessage;
        CurrentPhase = updated.CurrentPhase;
        CurrentPhaseStartedAt = updated.CurrentPhaseStartedAt;
        CanCancel = IsJobCancelable(updated);

        this.RaisePropertyChanged(nameof(Job));
        this.RaisePropertyChanged(nameof(HasError));
        this.RaisePropertyChanged(nameof(IsNuGetVerificationPending));
        this.RaisePropertyChanged(nameof(IsNuGetVerified));
        this.RaisePropertyChanged(nameof(HasCurrentPhase));
        this.RaisePropertyChanged(nameof(CurrentPhaseDisplay));
        this.RaisePropertyChanged(nameof(ElapsedText));
        this.RaisePropertyChanged(nameof(ShortCommitSha));
        ResolveWorkerName();
        PublishHeader();
    }

    /// <summary>
    /// Shows which worker ran the job. Worker names come from the admin-only worker list,
    /// so the short worker id is shown when the names cannot be read.
    /// </summary>
    private void ResolveWorkerName()
    {
        if (Job.WorkerId is not { } workerId || workerId == resolvedWorkerId)
            return;

        resolvedWorkerId = workerId;
        WorkerName = ShortId(workerId);
        PublishHeader();
        Observable.FromAsync(() => _client.GetWorkersAsync())
            .Select(workers => workers.FirstOrDefault(worker => worker.Id == workerId)?.Name)
            .Catch(Observable.Return<string?>(null))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(name =>
            {
                if (Job.WorkerId != workerId)
                    return;

                WorkerName = name;
                PublishHeader();
            })
            .DisposeWith(disposables);
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8];

    private void RefreshElapsed()
    {
        this.RaisePropertyChanged(nameof(ElapsedText));
        PublishHeader();
    }

    private void ApplyArtifacts(IReadOnlyList<PackageArtifact> artifacts)
    {
        if (Job.Kind != JobKind.PackageBuild)
            return;

        ObservableCollectionSync.Sync(
            Artifacts,
            artifacts,
            artifact => artifact.RelativePath,
            viewModel => viewModel.Artifact.RelativePath,
            artifact => new PackageArtifactViewModel(artifact, this),
            (viewModel, artifact) => viewModel.ApplyArtifactUpdate(artifact));

        this.RaisePropertyChanged(nameof(HasArtifacts));
    }

    private async Task SaveArtifactAsync(PackageArtifact artifact)
    {
        if (_fileSystemPicker is null)
        {
            ErrorMessage = "File saving is not available in this host.";
            this.RaisePropertyChanged(nameof(HasError));
            return;
        }

        var destination = await _fileSystemPicker.PickForSave(
            artifact.FileName,
            Maybe.From(DefaultExtension(artifact.FileName)),
            FileTypeFor(artifact.FileName),
            new FileTypeFilter("All files", ["*"]));

        if (destination.HasNoValue)
            return;

        var source = ByteSource.FromAsyncStreamFactory(() =>
            _client.OpenJobArtifactStreamAsync(Job.Id, artifact.RelativePath));

        var result = await destination.Value.SetContents(source);
        if (result.IsFailure)
        {
            ErrorMessage = result.Error;
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    private static string? DefaultExtension(string fileName)
    {
        var extension = IOPath.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension) ? null : extension.TrimStart('.');
    }

    private static FileTypeFilter FileTypeFor(string fileName)
    {
        var extension = IOPath.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension)
            ? new FileTypeFilter("Package files", ["*"])
            : new FileTypeFilter($"{extension.TrimStart('.').ToUpperInvariant()} files (*{extension})", [$"*{extension}"]);
    }

    private void ShowStatus(JobStatus status)
    {
        StatusText = FormatStatus(status);
        StatusIcon = new Icon(JobStatusDisplay.IconCode(status));
        StatusTone = JobStatusDisplay.Tone(status);
    }

    private static string FormatStatus(JobStatus status) => status switch
    {
        JobStatus.Queued => "Queued — Waiting for worker",
        JobStatus.Assigned => "Assigned",
        JobStatus.Running => "Running",
        JobStatus.Succeeded => "Succeeded",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        JobStatus.AwaitingNuGetIndex => "Waiting for NuGet indexing",
        _ => status.ToString()
    };

    private SectionHeader CreateHeader() => new(HeaderTitle, HeaderSubtitle, CreateHeaderActions());

    private HeaderAction[] CreateHeaderActions()
    {
        var actions = new List<HeaderAction>();

        if (CanCancel)
            actions.Add(new HeaderAction("Cancel", "mdi-stop-circle-outline", CancelJobCommand, isDestructive: true));

        actions.Add(new HeaderAction(DetailedLogButtonText, "mdi-text-box-search-outline", ToggleDetailedLogCommand));
        actions.Add(new HeaderAction("Refresh", "mdi-refresh", RefreshCommand));

        return actions.ToArray();
    }

    private string HeaderTitle => JobNaming.Title(Job);

    private string HeaderSubtitle
    {
        get
        {
            var parts = new List<string> { $"Status: {StatusText}" };

            if (HasCurrentPhase)
                parts.Add($"Current phase: {CurrentPhaseDisplay}");

            parts.Add($"Elapsed: {ElapsedText}");

            if (!string.IsNullOrEmpty(WorkerName))
                parts.Add($"Worker: {WorkerName}");

            if (ShortCommitSha is { } sha)
                parts.Add($"Commit: {sha}");

            if (IsStreaming)
                parts.Add("Streaming");

            if (Job.EnqueuedAt != default)
                parts.Add($"Queued: {LocalTimestampFormatter.Format(Job.EnqueuedAt)}");

            return string.Join(" · ", parts);
        }
    }

    private void PublishHeader()
    {
        if (isDisposed)
            return;

        // Recreating the header rebuilds every button, which drops open tooltips and
        // in-flight clicks; only do it when the set of actions actually changes.
        var actions = CreateHeaderActions();
        if (currentHeader.HasSameActionsAs(actions))
        {
            currentHeader.UpdateText(HeaderTitle, HeaderSubtitle);
            return;
        }

        currentHeader = new SectionHeader(HeaderTitle, HeaderSubtitle, actions);
        _header.OnNext(currentHeader);
    }

    private static bool IsJobCancelable(DeploymentJob job) =>
        job.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Assigned or JobStatus.AwaitingNuGetIndex
        && job.CancellationRequestedAt is null;

    public void Dispose()
    {
        isDisposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        disposables.Dispose();
        _terminalFeed = null;
        _filterSubscription.Dispose();
        _phaseTree.Dispose();
        _logs.Dispose();
        _header.Dispose();
    }

    public sealed class PackageArtifactViewModel : ReactiveObject
    {
        private readonly JobDetailViewModel parent;

        public PackageArtifactViewModel(PackageArtifact artifact, JobDetailViewModel parent)
        {
            Artifact = artifact;
            this.parent = parent;
            SaveCommand = ReactiveCommand.CreateFromTask(() => this.parent.SaveArtifactAsync(Artifact));
            SaveCommand.ThrownExceptions.NotifyWith(this.parent.notificationService, "Cannot save package");
        }

        public PackageArtifact Artifact { get; private set; }
        public string FileName => Artifact.FileName;
        public string SizeText => FormatSize(Artifact.SizeBytes);
        public string Sha256Text => string.IsNullOrWhiteSpace(Artifact.Sha256)
            ? ""
            : $"SHA-256 {Artifact.Sha256[..Math.Min(12, Artifact.Sha256.Length)]}";
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }

        public void ApplyArtifactUpdate(PackageArtifact updated)
        {
            Artifact = updated;
            this.RaisePropertyChanged(nameof(Artifact));
            this.RaisePropertyChanged(nameof(FileName));
            this.RaisePropertyChanged(nameof(SizeText));
            this.RaisePropertyChanged(nameof(Sha256Text));
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / 1024d / 1024d:0.0} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024d:0.0} KB";
            return $"{bytes} B";
        }
    }
}
