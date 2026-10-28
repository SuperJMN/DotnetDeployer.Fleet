using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>
/// Resolves the flexible references users and agents type: projects by name, id or Git URL;
/// jobs by full id or a unique id prefix.
/// </summary>
public static class FleetReferences
{
    public static Project ResolveProject(IReadOnlyList<Project> projects, string reference)
    {
        var trimmed = reference.Trim();
        if (Guid.TryParse(trimmed, out var id))
        {
            return projects.FirstOrDefault(p => p.Id == id)
                   ?? throw new FleetUsageException($"No project with id {id}.");
        }

        var byName = projects.Where(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];

        var normalizedUrl = NormalizeGitUrl(trimmed);
        var byUrl = projects.Where(p => NormalizeGitUrl(p.GitUrl) == normalizedUrl).ToList();
        if (byUrl.Count == 1) return byUrl[0];

        var matches = byName.Count > 1 ? byName : byUrl;
        if (matches.Count > 1)
            throw new FleetUsageException(
                $"'{reference}' matches several projects: {string.Join(", ", matches.Select(p => $"{p.Name} ({p.Id})"))}. Use the id.");

        throw new FleetUsageException($"No project named '{reference}' or with that Git URL. Run 'fleet projects list'.");
    }

    public static Project? FindProject(IReadOnlyList<Project> projects, string? name, string? gitUrl)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = projects.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        if (!string.IsNullOrWhiteSpace(gitUrl))
        {
            var normalized = NormalizeGitUrl(gitUrl);
            return projects.FirstOrDefault(p => NormalizeGitUrl(p.GitUrl) == normalized);
        }

        return null;
    }

    /// <summary>Matches a full job id or a unique prefix of it (with or without dashes, at least 4 characters).</summary>
    public static Guid ResolveJobId(IEnumerable<Guid> jobIds, string reference)
    {
        var trimmed = reference.Trim();
        if (Guid.TryParse(trimmed, out var id))
            return id;

        var prefix = trimmed.Replace("-", "").ToLowerInvariant();
        if (prefix.Length < 4 || !prefix.All(Uri.IsHexDigit))
            throw new FleetUsageException($"'{reference}' is not a job id or an id prefix of at least 4 hex characters.");

        var matches = jobIds.Where(j => j.ToString("N").StartsWith(prefix, StringComparison.Ordinal)).Distinct().ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FleetUsageException($"No job id starts with '{reference}'."),
            _ => throw new FleetUsageException(
                $"'{reference}' matches {matches.Count} jobs ({string.Join(", ", matches.Take(5).Select(m => m.ToString("N")[..12]))}…). Use a longer prefix.")
        };
    }

    public static string NormalizeGitUrl(string url)
    {
        var value = url.Trim().ToLowerInvariant();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) value = value[(scheme + 3)..];

        // user@host:owner/repo (scp-like SSH) → host/owner/repo
        var at = value.IndexOf('@');
        var slash = value.IndexOf('/');
        if (at >= 0 && (slash < 0 || at < slash))
            value = value[(at + 1)..];
        var colon = value.IndexOf(':');
        slash = value.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash))
            value = value[..colon] + "/" + value[(colon + 1)..].TrimStart('/');

        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.Ordinal)) value = value[..^4];
        return value;
    }
}
