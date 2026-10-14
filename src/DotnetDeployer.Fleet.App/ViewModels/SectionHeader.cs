using System.Windows.Input;

namespace DotnetDeployer.Fleet.App.ViewModels;

public sealed class SectionHeader
{
    public SectionHeader(string title, params HeaderAction[] actions)
        : this(title, subtitle: null, actions)
    {
    }

    public SectionHeader(string title, string? subtitle, params HeaderAction[] actions)
    {
        Title = title;
        Subtitle = subtitle;
        Actions = actions;
    }

    public string Title { get; }
    public string? Subtitle { get; }
    public IReadOnlyList<HeaderAction> Actions { get; }
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
}
