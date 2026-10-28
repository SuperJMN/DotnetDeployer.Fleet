namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>Reads the KEY=value entries of a .env file.</summary>
public static class EnvFile
{
    /// <summary>
    /// Returns the entries in file order. Blank lines, comments and lines without a name are
    /// skipped; when a name repeats, the last value wins, as it would when the file is sourced.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Parse(IEnumerable<string> lines)
    {
        var entries = new List<(string Name, string Value)>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in lines.Select(ParseLine).OfType<(string Name, string Value)>())
        {
            if (indexByName.TryGetValue(entry.Name, out var index))
            {
                entries[index] = entry;
                continue;
            }

            indexByName[entry.Name] = entries.Count;
            entries.Add(entry);
        }

        return entries;
    }

    private static (string Name, string Value)? ParseLine(string line)
    {
        var text = line.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('#'))
        {
            return null;
        }

        if (text.StartsWith("export ", StringComparison.Ordinal))
        {
            text = text["export ".Length..].TrimStart();
        }

        var separatorIndex = text.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return null;
        }

        var name = text[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var value = text[(separatorIndex + 1)..].Trim();
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                value = value[1..^1];
            }
        }

        return (name, value);
    }
}
