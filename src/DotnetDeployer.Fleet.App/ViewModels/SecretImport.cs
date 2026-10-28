namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>What importing a .env file will do to the existing secrets.</summary>
public sealed record SecretImportPlan(
    IReadOnlyList<(string Name, string Value)> ToCreate,
    IReadOnlyList<(string Name, string Value)> ToOverwrite)
{
    public bool IsEmpty => ToCreate.Count == 0 && ToOverwrite.Count == 0;

    public static SecretImportPlan For(IEnumerable<(string Name, string Value)> entries, IEnumerable<string> existingNames)
    {
        var existing = existingNames.ToHashSet(StringComparer.Ordinal);
        var all = entries.ToList();
        return new SecretImportPlan(
            all.Where(entry => !existing.Contains(entry.Name)).ToList(),
            all.Where(entry => existing.Contains(entry.Name)).ToList());
    }

    /// <summary>Lists what will be created and what will be overwritten, for the user to confirm.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (ToCreate.Count > 0)
        {
            parts.Add($"Creates {Count(ToCreate.Count)}:\n{Names(ToCreate.Select(entry => entry.Name))}");
        }

        if (ToOverwrite.Count > 0)
        {
            parts.Add($"Overwrites {Count(ToOverwrite.Count)} that already exist, replacing their values:\n{Names(ToOverwrite.Select(entry => entry.Name))}");
        }

        return string.Join("\n\n", parts);
    }

    internal static string Count(int count) => count == 1 ? "1 secret" : $"{count} secrets";

    internal static string Names(IEnumerable<string> names) => string.Join("\n", names.Select(name => $"• {name}"));
}

/// <summary>What an import actually did, including the entries the coordinator refused.</summary>
public sealed record SecretImportSummary(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Overwritten,
    IReadOnlyList<(string Name, string Reason)> Failed)
{
    public string Describe()
    {
        var parts = new List<string>
        {
            $"Created {SecretImportPlan.Count(Created.Count)}, overwrote {SecretImportPlan.Count(Overwritten.Count)}."
        };

        if (Failed.Count > 0)
        {
            parts.Add($"Could not import {SecretImportPlan.Count(Failed.Count)}:\n" +
                      string.Join("\n", Failed.Select(failure => $"• {failure.Name}: {failure.Reason}")));
        }

        return string.Join("\n\n", parts);
    }
}
