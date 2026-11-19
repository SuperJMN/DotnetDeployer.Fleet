using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.App.Views;

/// <summary>
/// Makes a whole <see cref="TableView"/> row open its item: a tap anywhere on the row, or Enter or
/// Space (the centre button of a TV remote) on the focused row, runs the item's
/// <see cref="IOpenable.OpenCommand"/>. Taps on buttons inside the row (such as a row menu) are left
/// to those buttons. Right moves from the row to its buttons, Left goes back to the row and from the
/// row to whatever is beside the table, so a remote reaches the row menu and the rest of the page too.
/// </summary>
public static class TableRowActivation
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TableView, bool>("IsEnabled", typeof(TableRowActivation));

    static TableRowActivation()
    {
        IsEnabledProperty.Changed.AddClassHandler<TableView>((table, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                // Typed loosely so a tap raised by automation, which may carry plain RoutedEventArgs, also opens the row.
                table.AddHandler(InputElement.TappedEvent, (EventHandler<RoutedEventArgs>)OnTapped);
                // The row handles Enter and Space itself to select, so handled events are needed too.
                table.AddHandler(InputElement.KeyDownEvent, OnKeyDown, handledEventsToo: true);
            }
            else
            {
                table.RemoveHandler(InputElement.TappedEvent, (EventHandler<RoutedEventArgs>)OnTapped);
                table.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            }
        });
    }

    public static bool GetIsEnabled(TableView table) => table.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TableView table, bool value) => table.SetValue(IsEnabledProperty, value);

    private static void OnTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Visual source || IsInsideButton(source))
            return;

        if (source.FindAncestorOfType<TableViewRow>(includeSelf: true)?.DataContext is IOpenable item)
            Open(item, e);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source)
            return;

        switch (e.Key)
        {
            case Key.Enter or Key.Space when source is TableViewRow { DataContext: IOpenable item }:
                Open(item, e);
                break;
            case Key.Right when source is TableViewRow row:
                e.Handled |= FirstButtonIn(row)?.Focus(NavigationMethod.Directional) == true;
                break;
            case Key.Left when source is not TableViewRow && source.FindAncestorOfType<TableViewRow>() is { } row:
                e.Handled |= row.Focus(NavigationMethod.Directional);
                break;
            // The list swallows Left on its rows even though it has nowhere to go, so move on to what is beside the table.
            case Key.Left when source is TableViewRow row &&
                               TopLevel.GetTopLevel(row)?.FocusManager?.FindNextElement(NavigationDirection.Left) is { } beside:
                e.Handled |= beside.Focus(NavigationMethod.Directional);
                break;
        }
    }

    private static Button? FirstButtonIn(TableViewRow row) =>
        row.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true });

    private static void Open(IOpenable item, RoutedEventArgs e)
    {
        if (!item.OpenCommand.CanExecute(null))
            return;

        item.OpenCommand.Execute(null);
        e.Handled = true;
    }

    private static bool IsInsideButton(Visual source) =>
        source.GetSelfAndVisualAncestors()
            .TakeWhile(visual => visual is not TableViewRow)
            .Any(visual => visual is Button or ToggleButton);
}
