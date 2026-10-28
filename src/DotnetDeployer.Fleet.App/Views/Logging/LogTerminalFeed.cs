using DotnetDeployer.Fleet.Core.Logging;

namespace DotnetDeployer.Fleet.App.Views.Logging;

/// <summary>
/// The single writer of a job's visible log lines into a terminal. It keeps the
/// terminal equal to <paramref name="lines"/> (already filtered by severity):
/// when the lines only grew at the end, just the new tail is written; any other
/// change (such as a severity filter change) empties the terminal through
/// <paramref name="clear"/> and writes the lines again.
/// </summary>
/// <param name="lines">The visible lines, in order.</param>
/// <param name="write">Writes text to the terminal.</param>
/// <param name="clear">Empties the terminal completely, scrollback included.</param>
internal sealed class LogTerminalFeed(IReadOnlyList<LogLine> lines, Action<string> write, Action clear)
{
    // Log lines are records, so identity (not value equality) tells two equal lines apart.
    private readonly List<LogLine> written = [];

    /// <summary>
    /// Brings the terminal up to date with the current lines. Call it once per
    /// change to the lines, after the change has been applied.
    /// </summary>
    public void Sync()
    {
        if (!StartsWithWrittenLines())
        {
            clear();
            written.Clear();
        }

        for (var i = written.Count; i < lines.Count; i++)
        {
            var line = lines[i];
            write(LogAnsi.Format(line) + "\r\n");
            written.Add(line);
        }
    }

    private bool StartsWithWrittenLines()
    {
        if (lines.Count < written.Count)
            return false;

        for (var i = 0; i < written.Count; i++)
        {
            if (!ReferenceEquals(lines[i], written[i]))
                return false;
        }

        return true;
    }
}
