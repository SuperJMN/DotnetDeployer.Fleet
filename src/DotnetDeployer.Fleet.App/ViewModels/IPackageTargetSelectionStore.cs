using System.IO;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Remembers, per project, the package targets ("format:architecture") last queued
/// from the "Queue build" dialog so the next build starts from the same selection.
/// </summary>
public interface IPackageTargetSelectionStore
{
    Maybe<IReadOnlyCollection<string>> Get(Guid projectId);
    void Save(Guid projectId, IEnumerable<string> targets);
}

public class InMemoryPackageTargetSelectionStore : IPackageTargetSelectionStore
{
    private readonly Dictionary<Guid, IReadOnlyCollection<string>> selections = [];

    public Maybe<IReadOnlyCollection<string>> Get(Guid projectId) =>
        selections.TryGetValue(projectId, out var targets) ? Maybe.From(targets) : Maybe<IReadOnlyCollection<string>>.None;

    public void Save(Guid projectId, IEnumerable<string> targets) => selections[projectId] = targets.ToList();
}

public class FilePackageTargetSelectionStore : IPackageTargetSelectionStore
{
    private static readonly string SelectionsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DotnetFleet", "package-targets.json");

    private Dictionary<Guid, List<string>> selections = [];

    public FilePackageTargetSelectionStore() => Load();

    public Maybe<IReadOnlyCollection<string>> Get(Guid projectId) =>
        selections.TryGetValue(projectId, out var targets)
            ? Maybe.From<IReadOnlyCollection<string>>(targets)
            : Maybe<IReadOnlyCollection<string>>.None;

    public void Save(Guid projectId, IEnumerable<string> targets)
    {
        selections[projectId] = targets.ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SelectionsFile)!);
            File.WriteAllText(SelectionsFile, JsonSerializer.Serialize(selections));
        }
        catch { /* remembering the selection is a convenience; never fail a build over it */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(SelectionsFile))
                selections = JsonSerializer.Deserialize<Dictionary<Guid, List<string>>>(File.ReadAllText(SelectionsFile)) ?? [];
        }
        catch { /* ignore corrupt selections */ }
    }
}
