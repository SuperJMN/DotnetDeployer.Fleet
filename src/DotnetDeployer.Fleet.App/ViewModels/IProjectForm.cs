namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// The project form shown in a dialog, both to add a project and to edit one.
/// </summary>
public interface IProjectForm
{
    IObservable<bool> CanSave { get; }

    /// <summary>True when the fields differ from what the form was opened with.</summary>
    bool HasChanges { get; }

    /// <summary>Saves the project and tells whether it worked; on failure the form shows the error.</summary>
    Task<bool> TrySaveAsync();
}
