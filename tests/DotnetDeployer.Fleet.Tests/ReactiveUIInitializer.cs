using System.Runtime.CompilerServices;
using ReactiveUI.Builder;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Initializes ReactiveUI once for the whole test assembly, so no test class depends on
/// another one having run first.
/// </summary>
internal static class ReactiveUIInitializer
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }
#pragma warning restore CA2255
}
