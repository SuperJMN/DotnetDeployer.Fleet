using System.Globalization;

namespace DotnetDeployer.Fleet.Core.Domain;

/// <summary>
/// Largest NuGet package the coordinator stages for a release. It matches the 250 MB that
/// NuGet.org accepts, so any package the feed would take can go through Fleet.
/// </summary>
public static class NuGetPackageSizeLimit
{
    public const long MaxBytes = 250L * 1024 * 1024;

    /// <summary>Returns why <paramref name="packageFile"/> cannot be staged, or null when it fits.</summary>
    public static string? Check(string packageFile, long sizeBytes) =>
        sizeBytes > MaxBytes
            ? $"NuGet package {packageFile} is {Describe(sizeBytes)}, over the {Describe(MaxBytes)} limit for release packages (the same limit NuGet.org applies)."
            : null;

    public static string Describe(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.#} MB");
}
