using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetDeployer.Fleet.App.ViewModels;

[Section(name: "Settings", icon: "mdi-cog-outline", sortIndex: 4)]
public sealed partial class SettingsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly CompositeDisposable disposables = [];

    [Reactive] private AppTheme _selectedTheme;

    public SettingsViewModel(
        AppBootstrapper bootstrapper,
        BackendStatusViewModel backend,
        ISettingsService settings,
        IThemeApplier themeApplier,
        INotificationService notificationService)
    {
        Backend = backend;
        _selectedTheme = settings.GetTheme();

        ChangeEndpointCommand = ReactiveCommand.CreateFromTask(bootstrapper.ShowConnect);
        disposables.Add(ChangeEndpointCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Connection failed")));

        LogoutCommand = ReactiveCommand.CreateFromTask(bootstrapper.Logout);
        disposables.Add(LogoutCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Sign out failed")));

        disposables.Add(this.WhenAnyValue(vm => vm.SelectedTheme)
            .Skip(1)
            .Subscribe(theme =>
            {
                settings.SetTheme(theme);
                themeApplier.Apply(theme);
            }));

        Header = Observable.Return<object>(new SectionHeader("Settings"));
    }

    public BackendStatusViewModel Backend { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public ReactiveCommand<Unit, Maybe<Result<FleetApiClient>>> ChangeEndpointCommand { get; }
    public ReactiveCommand<Unit, Maybe<Result<FleetApiClient>>> LogoutCommand { get; }
    public IObservable<object> Header { get; }

    public void Dispose() => disposables.Dispose();
}
