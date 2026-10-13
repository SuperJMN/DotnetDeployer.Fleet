using System.Reactive;
using System.Reflection;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Shell;

namespace DotnetDeployer.Fleet.App.Shell;

public partial class AppShellViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    [Reactive] private BackendHealth _backendHealth;
    [Reactive] private string? _backendHealthMessage;
    [Reactive] private string? _backendVersion;
    [Reactive] private string? _backendEndpoint;

    public AppShellViewModel(IShell shell, IBackendHealthMonitor health, AppBootstrapper bootstrapper, INotificationService notificationService)
    {
        Shell = shell;

        var current = health.Current;
        _backendHealth = current.State;
        _backendHealthMessage = current.Message;
        _backendVersion = current.Version;
        _backendEndpoint = current.Endpoint?.ToString().TrimEnd('/');

        var sub = health.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(s =>
            {
                BackendHealth = s.State;
                BackendHealthMessage = s.Message;
                BackendVersion = s.Version;
                this.RaisePropertyChanged(nameof(BackendDisplayVersion));
                this.RaisePropertyChanged(nameof(VersionsDiffer));
                this.RaisePropertyChanged(nameof(VersionMismatchMessage));
                BackendEndpoint = s.Endpoint?.ToString().TrimEnd('/');
            });
        _disposables.Add(sub);

        ReconnectCommand = ReactiveCommand.CreateFromTask(bootstrapper.ShowConnect);
        _disposables.Add(ReconnectCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Connection failed")));
    }

    public void EnsureInitialSection()
    {
        if (Shell.SelectedSection.Value is null)
        {
            Shell.GoToSection("Projects");
        }
    }

    public IShell Shell { get; }
    public ReactiveCommand<Unit, Maybe<Result<FleetApiClient>>> ReconnectCommand { get; }
    public string? BackendDisplayVersion => VersionDisplay.VisibleWithPrefix(BackendVersion);

    public string? AppVersion { get; } = typeof(AppShellViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public string? AppDisplayVersion => VersionDisplay.VisibleWithPrefix(AppVersion);
    public bool VersionsDiffer => VersionDisplay.Differ(AppVersion, BackendVersion);

    public string? VersionMismatchMessage => VersionsDiffer
        ? $"This app ({AppDisplayVersion}) and the coordinator ({BackendDisplayVersion}) run different versions. Update the older one so they match."
        : null;

    public void Dispose() => _disposables.Dispose();
}
