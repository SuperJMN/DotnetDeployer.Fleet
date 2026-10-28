using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Logging;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FirstErrorLocatorTests
{
    [Fact]
    public void Finds_the_beginning_of_the_first_error_line()
    {
        LogLine[] log =
        [
            new("Restoring packages", LogSeverity.None),
            new("Failed MyTests.Adds_numbers [12 ms]", LogSeverity.Error),
            new("dotnet test exited with code 1", LogSeverity.Error)
        ];

        var target = FirstErrorLocator.Find(log);

        target.Value.Should().Be(new LogSearchTarget("Failed MyTests.Adds_numbers [12 ms]", 0));
    }

    [Fact]
    public void Skips_earlier_matches_of_the_same_text()
    {
        LogLine[] log =
        [
            new("Running: error CS0103 check", LogSeverity.None),
            new("error CS0103", LogSeverity.Error)
        ];

        var target = FirstErrorLocator.Find(log);

        target.Value.Should().Be(new LogSearchTarget("error CS0103", 1));
    }

    [Fact]
    public void Searches_only_the_printable_start_of_long_lines()
    {
        var longLine = "System.InvalidOperationException: the sequence contains no elements\tat Foo";
        LogLine[] log = [new(longLine, LogSeverity.Error)];

        var target = FirstErrorLocator.Find(log);

        target.Value.Text.Should().Be(longLine[..40]);
    }

    [Fact]
    public void Finds_nothing_in_a_log_without_errors()
    {
        LogLine[] log =
        [
            new("Build succeeded", LogSeverity.None),
            new("warning CS8618", LogSeverity.Warning)
        ];

        FirstErrorLocator.Find(log).HasNoValue.Should().BeTrue();
    }
}
