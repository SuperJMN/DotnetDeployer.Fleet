using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DotnetDeployer.Fleet.App.Shell;

public partial class AppShellView : UserControl
{
    public AppShellView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += OnLoaded;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Arrow keys move focus between controls for every input device, not only for remotes and gamepads,
        // so a keyboard plugged into an Android TV or phone works like its remote.
        if (OperatingSystem.IsAndroid() && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            XYFocus.SetNavigationModes(topLevel, XYFocusNavigationModes.Enabled);
        }
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is AppShellViewModel vm)
                {
                    vm.EnsureInitialSection();
                }
            },
            DispatcherPriority.Background);
    }
}
