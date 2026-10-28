using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.Dialogs;
using DotnetDeployer.Fleet.App.Shell;
using DotnetDeployer.Fleet.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Zafiro.Avalonia.Dialogs;
using Zafiro.Avalonia.Dialogs.Implementations;
using Zafiro.Avalonia.Icons;
using Zafiro.Avalonia.Misc;
using Zafiro.Avalonia.Storage;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell;
using Zafiro.UI.Shell.Utils;

namespace DotnetDeployer.Fleet.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        IconControlProviderRegistry.Register(new OptrisIconControlProvider(), asDefault: true);

        var logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        var services = new ServiceCollection();

        var httpHandler = new UnauthorizedHandler();
        var streamingHandler = new UnauthorizedHandler();
        var unauthorizedSignal = httpHandler.Unauthorized.Merge(streamingHandler.Unauthorized);

        var fleetClient = new FleetApiClient(httpHandler, streamingHandler, unauthorizedSignal);
        services.AddSingleton(fleetClient);
        services.AddSingleton<ISettingsService>(
            OperatingSystem.IsBrowser() ? new InMemorySettingsService() : new FileSettingsService());
        services.AddSingleton<IPackageTargetSelectionStore>(
            OperatingSystem.IsBrowser() ? new InMemoryPackageTargetSelectionStore() : new FilePackageTargetSelectionStore());
        services.AddSingleton<IBackendHealthMonitor, BackendHealthMonitor>();

        services.AddSingleton<IShell, Zafiro.UI.Shell.Shell>();
        services.AddScoped<INavigator>(sp =>
            new Navigator(
                sp,
                CSharpFunctionalExtensions.Maybe<Serilog.ILogger>.From(logger),
                ReactiveUI.RxSchedulers.MainThreadScheduler));
        services.AddAllSectionsFromAttributes(logger);

        services.AddSingleton(DialogService.Create());
        services.AddSingleton<INotificationService>(sp => new NotificationDialog(sp.GetRequiredService<IDialog>()));
        services.AddSingleton<IFileSystemPicker>(_ => new AvaloniaFileSystemPicker(() =>
        {
            var lifetime = Current?.ApplicationLifetime;
            return lifetime switch
            {
                IClassicDesktopStyleApplicationLifetime desktop when desktop.MainWindow is not null
                    => TopLevel.GetTopLevel(desktop.MainWindow)!.StorageProvider,
                ISingleViewApplicationLifetime singleView when singleView.MainView is not null
                    => TopLevel.GetTopLevel(singleView.MainView)!.StorageProvider,
                _ => throw new InvalidOperationException("No top-level available for file picker."),
            };
        }));
        services.AddTransient<ConnectDialogViewModel>();
        services.AddTransient<LoginDialogViewModel>();
        services.AddSingleton<IFleetClientConnectionFlow, FleetClientConnectionFlow>();
        services.AddSingleton<IConnectedFleetClientContext, ConnectedFleetClientContext>();
        services.AddSingleton<AppBootstrapper>();
        services.AddSingleton<AppShellViewModel>();

        var provider = services.BuildServiceProvider();

        var shell = provider.GetRequiredService<IShell>();
        this.Connect(
            () => new AppShellView(),
            _ => provider.GetRequiredService<AppShellViewModel>(),
            () => new Window { Title = "DotnetDeployer.Fleet", Width = 1200, Height = 800 });

        var bootstrapper = provider.GetRequiredService<AppBootstrapper>();
        Dispatcher.UIThread.Post(async () => await bootstrapper.RunAsync(), DispatcherPriority.Background);

        base.OnFrameworkInitializationCompleted();
    }
}
