using System.Globalization;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tests;

public class LocalTimestampFormatterTests
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    [Fact]
    public void Utc_timestamp_is_shown_in_the_viewer_time_zone()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var finishedAt = new DateTimeOffset(2026, 10, 14, 15, 30, 0, TimeSpan.Zero);

        var text = LocalTimestampFormatter.Format(finishedAt, plusTwo, Spanish);

        text.Should().Be("14/10/2026 17:30");
    }

    [Fact]
    public void Missing_timestamp_is_shown_as_a_placeholder()
    {
        LocalTimestampFormatter.Format(null, TimeZoneInfo.Utc, Spanish).Should().Be("--");
    }

    [Fact]
    public void Finished_job_shows_when_it_finished()
    {
        var finishedAt = new DateTimeOffset(2026, 10, 14, 15, 30, 0, TimeSpan.Zero);
        var job = new DeploymentJob { EnqueuedAt = finishedAt.AddMinutes(-10), FinishedAt = finishedAt };

        var vm = new JobViewModel(job, client: null!, navigator: null!);

        vm.TimestampLabel.Should().Be("Finished");
        vm.TimestampText.Should().Be(LocalTimestampFormatter.Format(finishedAt));
    }

    [Fact]
    public void Unfinished_job_shows_when_it_was_queued()
    {
        var enqueuedAt = new DateTimeOffset(2026, 10, 14, 15, 30, 0, TimeSpan.Zero);
        var job = new DeploymentJob { EnqueuedAt = enqueuedAt };

        var vm = new JobViewModel(job, client: null!, navigator: null!);

        vm.TimestampLabel.Should().Be("Queued");
        vm.TimestampText.Should().Be(LocalTimestampFormatter.Format(enqueuedAt));
    }
}
