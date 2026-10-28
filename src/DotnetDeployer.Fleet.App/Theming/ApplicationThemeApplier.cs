using Avalonia;
using Avalonia.Styling;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.App.Theming;

public sealed class ApplicationThemeApplier(Application application) : IThemeApplier
{
    public void Apply(AppTheme theme) => application.RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };
}
