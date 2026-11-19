using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DotnetDeployer.Fleet.App.Views;

/// <summary>
/// Lets Space choose a menu item, as Enter does. The centre button of a TV remote reaches Avalonia
/// as Space, and menus only answer to Enter, so without this a remote opens a menu but cannot pick
/// anything from it.
/// </summary>
public static class MenuItemSpaceActivation
{
    private static bool registered;

    public static void Register()
    {
        if (registered)
            return;

        registered = true;
        InputElement.KeyDownEvent.AddClassHandler<MenuItem>(OnKeyDown);
    }

    private static void OnKeyDown(MenuItem item, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || !ReferenceEquals(e.Source, item))
            return;

        e.Handled = true;
        item.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = item,
            Key = Key.Enter,
            PhysicalKey = PhysicalKey.Enter,
            KeyDeviceType = e.KeyDeviceType,
        });
    }
}
