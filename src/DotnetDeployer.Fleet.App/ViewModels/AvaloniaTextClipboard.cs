using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CSharpFunctionalExtensions;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// The clipboard of the window or view that hosts the app, on desktop as well
/// as on single-view hosts such as Android or the browser.
/// </summary>
public sealed class AvaloniaTextClipboard : ITextClipboard
{
    public async Task<Result> SetText(string text)
    {
        var host = Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow as Visual,
            ISingleViewApplicationLifetime singleView => singleView.MainView,
            _ => null
        };

        var clipboard = host is null ? null : TopLevel.GetTopLevel(host)?.Clipboard;
        if (clipboard is null)
            return Result.Failure("The clipboard is not available in this host.");

        await clipboard.SetTextAsync(text);
        return Result.Success();
    }
}
