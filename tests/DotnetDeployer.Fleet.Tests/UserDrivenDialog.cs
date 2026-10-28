using System.Reactive;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI.Commands;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Dialog double driven by a scripted user. While the dialog stays open, each step looks at the
/// dialog's view model (and may type into it) and answers which option to press; the press is
/// awaited before the next step runs. The dialog ends as soon as an option closes or dismisses it.
/// </summary>
internal sealed class UserDrivenDialog(params Func<object?, OptionRole>[] steps) : IDialog
{
    public int Presses { get; private set; }

    public async Task<bool> Show<TViewModel>(
        Maybe<TViewModel> viewModel,
        Maybe<IObservable<string>> title,
        Func<Maybe<TViewModel>, ICloseable, IEnumerable<IOption>> optionsFactory,
        Maybe<object> icon = default,
        DialogTone tone = DialogTone.Neutral,
        DialogSize size = DialogSize.Auto)
    {
        var closeable = new RecordingCloseable();
        var options = optionsFactory(viewModel, closeable).ToList();

        foreach (var step in steps)
        {
            var role = step(viewModel.GetValueOrDefault());
            var option = options.First(o => o.Role == role);
            if (!await option.Command.CanExecute.FirstAsync())
                throw new InvalidOperationException($"The {role} option is disabled.");

            Presses++;
            await ((IEnhancedCommand<Unit, Unit>)option.Command).Execute(Unit.Default);

            if (closeable.State.HasValue)
                return closeable.State.Value;
        }

        throw new InvalidOperationException("The dialog is still open after the scripted user ran out of steps.");
    }

    private sealed class RecordingCloseable : ICloseable
    {
        public Maybe<bool> State { get; private set; }

        public void Close() => State = true;

        public void Dismiss() => State = false;
    }
}
