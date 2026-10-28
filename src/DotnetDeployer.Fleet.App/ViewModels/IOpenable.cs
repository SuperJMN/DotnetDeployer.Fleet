using System.Windows.Input;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>A list row that opens its own page when the user activates it.</summary>
public interface IOpenable
{
    ICommand OpenCommand { get; }
}
