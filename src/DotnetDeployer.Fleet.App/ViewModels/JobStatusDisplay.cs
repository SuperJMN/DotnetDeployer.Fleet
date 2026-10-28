using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>How a <see cref="JobStatus"/> is shown: its text, its icon and its tone.</summary>
public static class JobStatusDisplay
{
    public static string Text(JobStatus status, NuGetVerificationStatus? nuGetVerification = null) => status switch
    {
        JobStatus.Queued => "Queued",
        JobStatus.Assigned => "Assigned",
        JobStatus.Running => "Running",
        JobStatus.Succeeded => nuGetVerification switch
        {
            NuGetVerificationStatus.Pending => "Succeeded · verifying NuGet",
            NuGetVerificationStatus.Verified => "Succeeded · NuGet verified",
            _ => "Succeeded"
        },
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        JobStatus.AwaitingNuGetIndex => "Waiting for NuGet",
        _ => status.ToString()
    };

    public static string IconCode(JobStatus status, NuGetVerificationStatus? nuGetVerification) =>
        status == JobStatus.Succeeded && nuGetVerification == NuGetVerificationStatus.Verified
            ? "mdi-check-decagram"
            : IconCode(status);

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

    /// <summary>True while the job still waits for, or is using, a worker or the NuGet feed.</summary>
    public static bool IsActive(JobStatus status) =>
        status is JobStatus.Queued or JobStatus.Assigned or JobStatus.Running or JobStatus.AwaitingNuGetIndex;
}
