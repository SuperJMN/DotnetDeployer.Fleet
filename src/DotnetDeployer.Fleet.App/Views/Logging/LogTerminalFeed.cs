using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DotnetDeployer.Fleet.Core.Logging;

namespace DotnetDeployer.Fleet.App.Views.Logging;

/// <summary>
/// The single writer of a job's visible log lines into a terminal. It mirrors
/// <paramref name="lines"/> (already filtered by severity): added lines are
/// appended once, and any other change redraws the terminal from scratch.
/// </summary>
internal sealed class LogTerminalFeed : IDisposable
{
    public const string ClearScreen = "\x1b[2J\x1b[H";

    private readonly ReadOnlyObservableCollection<LogLine> lines;
    private readonly Action<string> write;

    public LogTerminalFeed(ReadOnlyObservableCollection<LogLine> lines, Action<string> write)
    {
        this.lines = lines;
        this.write = write;

        Redraw();
        ((INotifyCollectionChanged)lines).CollectionChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args is { Action: NotifyCollectionChangedAction.Add, NewItems: not null })
        {
            Write(args.NewItems.Cast<LogLine>());
            return;
        }

        Redraw();
    }

    private void Redraw()
    {
        write(ClearScreen);
        Write(lines);
    }

    private void Write(IEnumerable<LogLine> toWrite)
    {
        foreach (var line in toWrite)
            write(LogAnsi.Format(line) + "\r\n");
    }

    public void Dispose() => ((INotifyCollectionChanged)lines).CollectionChanged -= OnLinesChanged;
}
