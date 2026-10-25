using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia.Android;

namespace DotnetDeployer.Fleet.Android;

[Activity(
    Label = "DotnetDeployer.Fleet",
    Icon = "@mipmap/icon",
    Theme = "@style/MyTheme.Splash",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
                         | ConfigChanges.ScreenSize
                         | ConfigChanges.UiMode)]
[IntentFilter([Intent.ActionMain], Categories = [Intent.CategoryLeanbackLauncher])]
public class MainActivity : AvaloniaMainActivity
{
}
