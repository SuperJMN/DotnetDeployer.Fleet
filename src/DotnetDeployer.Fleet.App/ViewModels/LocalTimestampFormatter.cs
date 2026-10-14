using System.Globalization;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Turns the UTC timestamps reported by the coordinator into the viewer's local wall-clock
/// time, formatted with the viewer's regional settings.
/// </summary>
internal static class LocalTimestampFormatter
{
    public static string Format(DateTimeOffset? timestamp) =>
        Format(timestamp, TimeZoneInfo.Local, CultureInfo.CurrentCulture);

    public static string Format(DateTimeOffset? timestamp, TimeZoneInfo timeZone, IFormatProvider culture)
    {
        if (timestamp is null)
            return "--";

        return TimeZoneInfo.ConvertTime(timestamp.Value, timeZone).ToString("g", culture);
    }
}
