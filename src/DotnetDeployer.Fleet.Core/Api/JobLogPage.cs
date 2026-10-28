using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Core.Api;

/// <summary>One stored log line of a job. <see cref="Number"/> is 1-based and stable for the job.</summary>
public sealed record JobLogLine(int Number, DateTimeOffset Timestamp, string Text);

/// <summary>
/// A finite snapshot of a job's log, unlike the SSE stream, which stays open while the job runs.
/// </summary>
public sealed record JobLogPage(Guid JobId, JobStatus Status, int TotalLines, IReadOnlyList<JobLogLine> Lines)
{
    /// <summary>
    /// Builds a page from the job's stored lines (oldest first). <paramref name="since"/> keeps only
    /// lines with a number greater than it; <paramref name="tail"/> then keeps the last N of those.
    /// </summary>
    public static JobLogPage From(Guid jobId, JobStatus status, IReadOnlyList<LogEntry> entries, int? since, int? tail)
    {
        IEnumerable<JobLogLine> lines = entries
            .Select((entry, index) => new JobLogLine(index + 1, entry.Timestamp, entry.Line));

        if (since is > 0)
            lines = lines.Where(line => line.Number > since.Value);

        var selected = lines.ToList();
        if (tail is >= 0 && selected.Count > tail.Value)
            selected = selected.GetRange(selected.Count - tail.Value, tail.Value);

        return new JobLogPage(jobId, status, entries.Count, selected);
    }
}
