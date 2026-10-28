using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Core.Logging;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Where the first error of a log is, expressed as a terminal search: the text to look for
/// and which of its matches (zero-based) is the error line itself.
/// </summary>
internal sealed record LogSearchTarget(string Text, int MatchIndex);

/// <summary>
/// Finds the first error line of a log so the log viewer can jump to it. The terminal
/// searches row by row, so the target is the beginning of the line (which always starts a
/// row) and the match index skips identical text that appears earlier in the log.
/// </summary>
internal static class FirstErrorLocator
{
    private const int MaxSearchTextLength = 40;

    public static Maybe<LogSearchTarget> Find(IReadOnlyList<LogLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Severity < LogSeverity.Error)
                continue;

            var searchText = SearchTextOf(lines[i].Text);
            if (string.IsNullOrWhiteSpace(searchText))
                continue;

            var earlierMatches = lines.Take(i).Sum(line => CountMatches(line.Text, searchText));
            return new LogSearchTarget(searchText, earlierMatches);
        }

        return Maybe<LogSearchTarget>.None;
    }

    private static string SearchTextOf(string text)
    {
        var controlIndex = text.IndexOfAny(['\t', '\r', '\n', '\x1b']);
        var printable = controlIndex >= 0 ? text[..controlIndex] : text;
        return printable.Length > MaxSearchTextLength ? printable[..MaxSearchTextLength] : printable;
    }

    private static int CountMatches(string text, string searchText)
    {
        var count = 0;
        for (var index = text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf(searchText, index + searchText.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }
}
