namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>Colour scheme chosen in Settings. <see cref="System"/> follows the operating system.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>Switches the running application to a theme.</summary>
public interface IThemeApplier
{
    void Apply(AppTheme theme);
}
