using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.App.Views;

/// <summary>
/// Makes a whole <see cref="TableView"/> row open its item: a tap anywhere on the row, or Enter
/// on the selected row, runs the item's <see cref="IOpenable.OpenCommand"/>. Taps on buttons
/// inside the row (such as a row menu) are left to those buttons.
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
                table.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
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
        if (e.Key == Key.Enter && sender is TableView { SelectedItem: IOpenable item })
            Open(item, e);
    }

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
