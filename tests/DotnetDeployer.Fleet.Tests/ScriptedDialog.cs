using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Zafiro.Avalonia.Dialogs;
using Zafiro.Avalonia.Dialogs.Views;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Dialog double that answers like a user would: it presses the destructive (or, failing that,
/// primary) option when <paramref name="confirm"/> is true and the cancel option otherwise. Dialogs without those
/// options (plain messages) get their first option pressed.
/// </summary>
internal sealed class ScriptedDialog(bool confirm) : IDialog
{
    public List<(string Title, string? Message)> Shown { get; } = [];

    public async Task<bool> Show<TViewModel>(
        Maybe<TViewModel> viewModel,
        Maybe<IObservable<string>> title,
        Func<Maybe<TViewModel>, ICloseable, IEnumerable<IOption>> optionsFactory,
        Maybe<object> icon = default,
        DialogTone tone = DialogTone.Neutral,
        DialogSize size = DialogSize.Auto)
    {
        var titleText = title.HasValue ? await title.Value.FirstAsync() : "";
        var message = viewModel.HasValue && viewModel.Value is MessageDialogViewModel messageViewModel
            ? messageViewModel.Message
            : null;
        Shown.Add((titleText, message));

        var closeable = new RecordingCloseable();
        var options = optionsFactory(viewModel, closeable).ToList();
        var choice = confirm
            ? options.FirstOrDefault(option => option.Role == OptionRole.Destructive)
              ?? options.FirstOrDefault(option => option.Role == OptionRole.Primary)
            : options.FirstOrDefault(option => option.Role == OptionRole.Cancel);
        choice ??= options.First();
        choice.Command.Execute(null);

        return closeable.IsClosed;
    }

    private sealed class RecordingCloseable : ICloseable
    {
        public bool IsClosed { get; private set; }

        public void Close() => IsClosed = true;

        public void Dismiss() => IsClosed = false;
    }
}
