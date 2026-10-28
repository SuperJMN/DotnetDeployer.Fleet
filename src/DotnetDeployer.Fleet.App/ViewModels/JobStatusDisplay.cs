using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>How a <see cref="JobStatus"/> is shown: its icon and its tone.</summary>
public static class JobStatusDisplay
{
    public static string IconCode(JobStatus status) => status switch
    {
        JobStatus.Queued => "mdi-clock-outline",
        JobStatus.Assigned => "mdi-account-arrow-right-outline",
        JobStatus.Running => "mdi-rocket-launch",
        JobStatus.Succeeded => "mdi-check-circle",
        JobStatus.Failed => "mdi-alert-circle",
        JobStatus.Cancelled => "mdi-cancel",
        JobStatus.AwaitingNuGetIndex => "mdi-clock-outline",
        _ => "mdi-help-circle-outline"
    };

    public static Tone Tone(JobStatus status) => status switch
    {
        JobStatus.Assigned or JobStatus.Running or JobStatus.AwaitingNuGetIndex => ViewModels.Tone.Info,
        JobStatus.Succeeded => ViewModels.Tone.Success,
        JobStatus.Failed => ViewModels.Tone.Danger,
        JobStatus.Cancelled => ViewModels.Tone.Warning,
        _ => ViewModels.Tone.Neutral
    };
}
