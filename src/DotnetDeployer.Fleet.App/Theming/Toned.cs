using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using DotnetDeployer.Fleet.App.ViewModels;
using Zafiro.Avalonia.Controls;

namespace DotnetDeployer.Fleet.App.Theming;

/// <summary>
/// Paints a control with the theme brush of a <see cref="ViewModels.Tone"/>: its text and icons,
/// or its fill when it is a shape. The brush is looked up as <c>Tone{Tone}Brush</c> and follows
/// theme changes, which a converter returning a brush could not do.
/// </summary>
public sealed class Toned
{
    public static readonly AttachedProperty<Tone?> ToneProperty =
        AvaloniaProperty.RegisterAttached<Control, Tone?>("Tone", typeof(Toned));

    private static readonly AttachedProperty<IDisposable?> BrushBindingProperty =
        AvaloniaProperty.RegisterAttached<Control, IDisposable?>("BrushBinding", typeof(Toned));

    static Toned() => ToneProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));

    private Toned()
    {
    }

    public static Tone? GetTone(Control control) => control.GetValue(ToneProperty);

    public static void SetTone(Control control, Tone? value) => control.SetValue(ToneProperty, value);

    public static string BrushKey(Tone tone) => $"Tone{tone}Brush";

    private static void Apply(Control control)
    {
        control.GetValue(BrushBindingProperty)?.Dispose();
        control.ClearValue(BrushBindingProperty);

        if (GetTone(control) is not { } tone)
            return;

        var brush = control.GetResourceObservable(BrushKey(tone));
        var target = control is Shape ? Shape.FillProperty : TextElement.ForegroundProperty;
        control.SetValue(BrushBindingProperty, new CompositeDisposable(
            control.Bind(target, brush),
            control.Bind(IconOptions.FillProperty, brush)));
    }
}
