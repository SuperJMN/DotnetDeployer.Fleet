using System.Windows.Input;
using ReactiveUI;

namespace DotnetDeployer.Fleet.App.ViewModels;

public sealed class SectionHeader : ReactiveObject
{
    private string title;
    private string? subtitle;

    public SectionHeader(string title, params HeaderAction[] actions)
        : this(title, subtitle: null, actions)
    {
    }

    public SectionHeader(string title, string? subtitle, params HeaderAction[] actions)
    {
        this.title = title;
        this.subtitle = subtitle;
        Actions = actions;
    }

    public string Title
    {
        get => title;
        private set => this.RaiseAndSetIfChanged(ref title, value);
    }

    public string? Subtitle
    {
        get => subtitle;
        private set => this.RaiseAndSetIfChanged(ref subtitle, value);
    }

    public IReadOnlyList<HeaderAction> Actions { get; }

    /// <summary>
    /// Updates the texts in place, so the rendered header (and its buttons) survive
    /// changes that only affect the title or the subtitle.
    /// </summary>
    public void UpdateText(string newTitle, string? newSubtitle)
    {
        Title = newTitle;
        Subtitle = newSubtitle;
    }

    public bool HasSameActionsAs(IReadOnlyList<HeaderAction> actions) =>
        Actions.Count == actions.Count && Actions.Zip(actions).All(pair => pair.First.IsEquivalentTo(pair.Second));
}

public sealed class HeaderAction
{
    public HeaderAction(string text, string iconCode, ICommand command, bool isPrimary = false, bool isDestructive = false)
    {
        Text = text;
        IconCode = iconCode;
        Command = command;
        IsPrimary = isPrimary;
        IsDestructive = isDestructive;
    }

    public string Text { get; }
    public string IconCode { get; }
    public ICommand Command { get; }
    public bool IsPrimary { get; }
    public bool IsDestructive { get; }
    public bool IsPrimaryCommand => IsPrimary && !IsDestructive;
    public bool IsDestructiveCommand => IsDestructive;
    public bool IsSecondaryCommand => !IsPrimary && !IsDestructive;

    public bool IsEquivalentTo(HeaderAction other) =>
        Text == other.Text
        && IconCode == other.IconCode
        && ReferenceEquals(Command, other.Command)
        && IsPrimary == other.IsPrimary
        && IsDestructive == other.IsDestructive;
}
