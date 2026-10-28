namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Meaning of a status, independent of how it is painted. Views turn it into a colour from
/// the current theme, so status colours follow light and dark mode.
/// </summary>
public enum Tone
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger
}
