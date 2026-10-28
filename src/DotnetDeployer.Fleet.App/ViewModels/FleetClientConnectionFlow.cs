using Avalonia.Threading;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.App.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI.Commands;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Shows the connect and sign-in dialogs. A dialog only closes once its action succeeds: when it fails,
/// the dialog stays open with whatever the user typed and the reason shown inside, so the user can
/// correct it and retry or cancel.
/// </summary>
public sealed class FleetClientConnectionFlow : IFleetClientConnectionFlow
{
    private readonly IServiceProvider services;
    private readonly IDialog dialog;
    private readonly Func<Task> waitForDialogToClose;

    public FleetClientConnectionFlow(IServiceProvider services, IDialog dialog)
        : this(services, dialog, () => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask())
    {
    }

    internal FleetClientConnectionFlow(IServiceProvider services, IDialog dialog, Func<Task> waitForDialogToClose)
    {
        this.services = services;
        this.dialog = dialog;
        this.waitForDialogToClose = waitForDialogToClose;
    }

    public Task<Maybe<Result>> Connect()
    {
        var viewModel = services.GetRequiredService<ConnectDialogViewModel>();
        return Show(viewModel, "Coordinator endpoint", "Connect", x => x.TryConnect(), x => x.CanConnect);
    }

    public Task<Maybe<Result>> Login()
    {
        var viewModel = services.GetRequiredService<LoginDialogViewModel>();
        return Show(viewModel, "Sign in", "Sign in", x => x.TryLogin(), x => x.CanLogin);
    }

    private async Task<Maybe<Result>> Show<TViewModel>(
        TViewModel viewModel,
        string title,
        string actionText,
        Func<TViewModel, Task<Result>> action,
        Func<TViewModel, IObservable<bool>> canExecute)
        where TViewModel : class
    {
        var completed = await dialog.Show<TViewModel>(viewModel, title, (vm, closeable) =>
        {
            var command = ReactiveCommand.CreateFromTask(async () =>
            {
                var result = await action(vm);
                if (result.IsSuccess)
                    closeable.Close();
            }, canExecute(vm)).Enhance();

            return new IOption[]
            {
                new Option("Cancel",
                    ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                    new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                new Option(actionText,
                    command,
                    new Settings { IsDefault = true, Role = OptionRole.Primary }),
            };
        });

        if (!completed)
            return Maybe<Result>.None;

        await waitForDialogToClose();

        return Maybe.From(Result.Success());
    }
}
