using CSharpFunctionalExtensions;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Puts text on the system clipboard.
/// </summary>
public interface ITextClipboard
{
    /// <summary>Copies <paramref name="text"/>, or fails when this host has no clipboard.</summary>
    Task<Result> SetText(string text);
}
