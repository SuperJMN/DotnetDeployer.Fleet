using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public sealed class RelativeTimeFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(2 * 3600 + 600, "2 h ago")]
    [InlineData(3 * 86400, "3 d ago")]
    public void Recent_moments_read_as_time_ago(int secondsAgo, string expected)
    {
        RelativeTimeFormatter.Format(Now.AddSeconds(-secondsAgo), Now).Should().Be(expected);
    }

    [Fact]
    public void A_moment_ahead_of_the_local_clock_reads_as_just_now()
    {
        RelativeTimeFormatter.Format(Now.AddMinutes(3), Now).Should().Be("just now");
    }

    [Fact]
    public void Moments_older_than_a_month_show_their_date()
    {
        var timestamp = Now.AddDays(-45);

        RelativeTimeFormatter.Format(timestamp, Now).Should().Be(LocalTimestampFormatter.Format(timestamp));
    }
}
