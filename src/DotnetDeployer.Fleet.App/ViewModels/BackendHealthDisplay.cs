namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>How a <see cref="BackendHealth"/> is shown: its label and its tone.</summary>
public static class BackendHealthDisplay
{
    public static string Label(BackendHealth state) => state switch
    {
        BackendHealth.Healthy => "Connected",
        BackendHealth.Checking => "Checking…",
        BackendHealth.Unreachable => "No connectivity",
        BackendHealth.Error => "Error",
        _ => "Unknown"
    };

    public static Tone Tone(BackendHealth state) => state switch
    {
        BackendHealth.Healthy => ViewModels.Tone.Success,
        BackendHealth.Checking => ViewModels.Tone.Warning,
        BackendHealth.Unreachable or BackendHealth.Error => ViewModels.Tone.Danger,
        _ => ViewModels.Tone.Neutral
    };
}
