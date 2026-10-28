using System.Runtime.CompilerServices;
using ReactiveUI.Builder;

/// <summary>
/// Initialises ReactiveUI once for the whole test assembly, before any test runs.
/// A per-class static constructor is not enough: if a class that touches
/// WhenAnyValue runs first without it, ReactiveUI's type initialiser fails and
/// stays broken for the rest of the process.
/// </summary>
internal static class ReactiveUiInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }
}
