namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Says how long ago something happened in the short form used by lists, such as "5 min ago"
/// or "2 h ago". Moments older than a month fall back to the local date and time.
/// </summary>
internal static class RelativeTimeFormatter
{
    public static string Format(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var elapsed = now - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1))
            return "just now";

        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes} min ago";

        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours} h ago";

        if (elapsed < TimeSpan.FromDays(30))
            return $"{(int)elapsed.TotalDays} d ago";

        return LocalTimestampFormatter.Format(timestamp);
    }
}
