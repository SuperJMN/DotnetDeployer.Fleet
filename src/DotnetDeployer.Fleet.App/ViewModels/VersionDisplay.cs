namespace DotnetDeployer.Fleet.App.ViewModels;

internal static class VersionDisplay
{
    public static string? Visible(string? version)
    {
        if (version is null)
            return null;

        var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex < 0 ? version : version[..metadataIndex];
    }

    public static string? VisibleWithPrefix(string? version)
    {
        var visible = Visible(version);
        return visible is null || visible.Length == 0 ? visible : $"v{visible}";
    }

    /// <summary>
    /// True when both versions are known and their visible parts differ. Build metadata is
    /// ignored, so two builds of the same release are not reported as different.
    /// </summary>
    public static bool Differ(string? version, string? other)
    {
        var visible = Visible(version);
        var otherVisible = Visible(other);
        return !string.IsNullOrEmpty(visible)
               && !string.IsNullOrEmpty(otherVisible)
               && !string.Equals(visible, otherVisible, StringComparison.Ordinal);
    }
}
