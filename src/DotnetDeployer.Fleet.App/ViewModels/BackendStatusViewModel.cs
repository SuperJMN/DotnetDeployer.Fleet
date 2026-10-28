using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// What the app knows about the coordinator it talks to: whether it is reachable, where it
/// lives and which version it runs, next to the version of this app.
/// </summary>
public sealed partial class BackendStatusViewModel : ReactiveObject, IDisposable
{
    public static readonly string? CurrentAppVersion = typeof(BackendStatusViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private readonly IDisposable subscription;

    [Reactive] private BackendHealth _health;
    [Reactive] private string? _message;
    [Reactive] private string? _version;
    [Reactive] private string? _endpoint;

    public BackendStatusViewModel(IBackendHealthMonitor monitor)
        : this(monitor, CurrentAppVersion, RxSchedulers.MainThreadScheduler)
    {
    }

    public BackendStatusViewModel(IBackendHealthMonitor monitor, string? appVersion, IScheduler scheduler)
    {
        AppVersion = appVersion;
        Show(monitor.Current);
        subscription = monitor.Snapshots
            .ObserveOn(scheduler)
            .Subscribe(Show);
    }

    public string HealthLabel => BackendHealthDisplay.Label(Health);
    public Tone HealthTone => BackendHealthDisplay.Tone(Health);

    public bool HasEndpoint => !string.IsNullOrWhiteSpace(Endpoint);
    public string EndpointText => HasEndpoint ? Endpoint! : "Not configured";

    public string? DisplayVersion => VersionDisplay.VisibleWithPrefix(Version);

    public string? AppVersion { get; }
    public string? AppDisplayVersion => VersionDisplay.VisibleWithPrefix(AppVersion);

    public bool VersionsDiffer => VersionDisplay.Differ(AppVersion, Version);

    public string? VersionMismatchMessage => VersionsDiffer
        ? $"This app ({AppDisplayVersion}) and the coordinator ({DisplayVersion}) run different versions. Update the older one so they match."
        : null;

    public void Dispose() => subscription.Dispose();

    private void Show(BackendHealthSnapshot snapshot)
    {
        Health = snapshot.State;
        Message = snapshot.Message;
        Version = snapshot.Version;
        Endpoint = snapshot.Endpoint?.ToString().TrimEnd('/');
        this.RaisePropertyChanged(nameof(HealthLabel));
        this.RaisePropertyChanged(nameof(HealthTone));
        this.RaisePropertyChanged(nameof(HasEndpoint));
        this.RaisePropertyChanged(nameof(EndpointText));
        this.RaisePropertyChanged(nameof(DisplayVersion));
        this.RaisePropertyChanged(nameof(VersionsDiffer));
        this.RaisePropertyChanged(nameof(VersionMismatchMessage));
    }
}
