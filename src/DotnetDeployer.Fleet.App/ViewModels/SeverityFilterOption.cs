using DotnetDeployer.Fleet.Core.Logging;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// One choice of the log severity filter: a readable label and the lowest
/// severity it lets through.
/// </summary>
public sealed record SeverityFilterOption(string Label, LogSeverity Minimum)
{
    public static IReadOnlyList<SeverityFilterOption> All { get; } =
    [
        new("All lines", LogSeverity.None),
        new("Info and above", LogSeverity.Info),
        new("Warnings and errors", LogSeverity.Warning),
        new("Errors only", LogSeverity.Error)
    ];

    public override string ToString() => Label;
}
