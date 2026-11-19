using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;
using Zafiro.Avalonia.Mcp.AppHost;

namespace DotnetDeployer.Fleet.Android;

[Application]
public class DotnetDeployerFleetAndroidApplication : AvaloniaAndroidApplication<global::DotnetDeployer.Fleet.App.App>
{
    public DotnetDeployerFleetAndroidApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .UseReactiveUI(_ => { })
            .WithInterFont()
            .UseMcpDiagnostics()
            .LogToTrace();
    }
}
