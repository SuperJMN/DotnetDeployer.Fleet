using System.Reactive;
using System.Reactive.Disposables;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using ReactiveUI;
using Zafiro.UI;
using Zafiro.UI.Shell;

namespace DotnetDeployer.Fleet.App.Shell;

public class AppShellViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    public AppShellViewModel(IShell shell, BackendStatusViewModel backend, AppBootstrapper bootstrapper, INotificationService notificationService)
    {
        Shell = shell;
        Backend = backend;

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
    public BackendStatusViewModel Backend { get; }
    public ReactiveCommand<Unit, Maybe<Result<FleetApiClient>>> ReconnectCommand { get; }

    public void Dispose() => _disposables.Dispose();
}
