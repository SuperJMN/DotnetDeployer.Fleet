using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class NuGetPackagePusher
{
    private static readonly string[] NonNuGetPublishSecrets = ["GITHUB_TOKEN", "GH_TOKEN"];

    public static async Task<(bool Success, string? Error)> PushAsync(
        string workingDirectory,
        string packagePath,
        string apiKey,
        string source,
        Func<string, Task> onLine,
        CancellationToken ct = default)
    {
        return await PushAsync(workingDirectory, packagePath, apiKey, source, onLine, StreamingProcessRunner.Instance, ct);
    }

    public static async Task<(bool Success, string? Error)> PushAsync(
        string workingDirectory,
        string packagePath,
        string apiKey,
        string source,
        Func<string, Task> onLine,
        IStreamingProcessRunner processRunner,
        CancellationToken ct = default)
    {
        var packageFileName = Path.GetFileName(packagePath);

        // Pre-push conflict check for local/folder feeds:
        // If the package already exists, verify byte identity (SHA256).
        // If content differs, FAIL CLOSED to prevent serving incorrect revision bytes.
        if (Directory.Exists(source))
        {
            var existingCandidates = Directory.GetFiles(source, packageFileName, SearchOption.AllDirectories);
            if (existingCandidates.Length > 0)
            {
                var existingFile = existingCandidates[0];
                var localHash = ComputeFileSha256(packagePath);
                var existingHash = ComputeFileSha256(existingFile);

                if (!string.Equals(localHash, existingHash, StringComparison.OrdinalIgnoreCase))
                {
                    var conflictError = $"Conflict: Package '{packageFileName}' already exists in feed '{source}' with different contents (hash mismatch: local {localHash[..12]} vs feed {existingHash[..12]}). Release aborted to prevent serving incorrect revision bytes.";
                    await onLine($"[nuget.push] CONFLICT: {conflictError}");
                    return (false, conflictError);
                }

                await onLine($"[nuget.push] Package '{packageFileName}' already exists in feed '{source}' with identical contents (SHA256 {localHash[..12]}). Idempotent release verified.");
                return (true, null);
            }
        }

        var isFolderFeed = Directory.Exists(source);
        var pushArgs = new List<string> { "nuget", "push", packagePath, "--api-key", apiKey, "--source", source };
        if (isFolderFeed)
        {
            pushArgs.Add("--skip-duplicate");
        }
        else if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            pushArgs.Add("--allow-insecure-connections");
        }

        var psi = DeployerRunner.CreateDotnetProcessStartInfo(
            workingDirectory,
            pushArgs,
            envVars: null,
            scrubKeys: NonNuGetPublishSecrets);

        var duplicateConflictDetected = false;
        int? authorizationStatus = null;
        (int Status, string Reason)? packageRejection = null;

        var exitCode = await processRunner.RunAsync(psi, async line =>
        {
            authorizationStatus ??= TryParseAuthorizationStatus(line);
            packageRejection ??= TryParsePackageRejection(line);

            if (line.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Conflict", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("409", StringComparison.Ordinal))
            {
                duplicateConflictDetected = true;
            }

            var sanitized = !string.IsNullOrEmpty(apiKey)
                ? line.Replace(apiKey, "***HIDDEN***", StringComparison.Ordinal)
                : line;
            await onLine(sanitized).ConfigureAwait(false);
        }, ct);

        if (exitCode != 0)
        {
            if (authorizationStatus is { } status)
            {
                var rejected = $"{AuthorizationRejectionPrefix}'{source}' rejected the API key for '{packageFileName}' (HTTP {status}). Check that the key has not expired, can push new packages and package versions, and that its glob pattern covers this package.";
                await onLine($"[nuget.push] HTTP {status}: {rejected}");
                return (false, rejected);
            }

            if (duplicateConflictDetected)
            {
                var unverified = $"{DuplicateRejectionPrefix}Package '{packageFileName}' returned HTTP 409 from '{source}'; its remote contents must be verified after it becomes downloadable.";
                await onLine($"[nuget.push] HTTP 409: {unverified}");
                return (false, unverified);
            }

            if (packageRejection is { } rejection)
            {
                var rejected = $"{PackageRejectionPrefix}'{source}' refused '{packageFileName}' (HTTP {rejection.Status}): {rejection.Reason}";
                await onLine($"[nuget.push] HTTP {rejection.Status}: {rejected}");
                return (false, rejected);
            }

            return (false, $"dotnet nuget push for '{packageFileName}' exited with code {exitCode}");
        }

        // Post-push verification for local folder feeds:
        if (isFolderFeed)
        {
            var pushedCandidates = Directory.GetFiles(source, packageFileName, SearchOption.AllDirectories);
            if (pushedCandidates.Length > 0)
            {
                var feedHash = ComputeFileSha256(pushedCandidates[0]);
                var localHash = ComputeFileSha256(packagePath);
                if (!string.Equals(localHash, feedHash, StringComparison.OrdinalIgnoreCase))
                {
                    var conflictError = $"Conflict: Package '{packageFileName}' in feed '{source}' has different contents after push (hash mismatch: local {localHash[..12]} vs feed {feedHash[..12]}).";
                    await onLine($"[nuget.push] CONFLICT: {conflictError}");
                    return (false, conflictError);
                }
            }
        }
        else if (duplicateConflictDetected)
        {
            // For remote feeds, if duplicate or conflict was detected, NEVER declare success
            // without verifying remote artifact identity.
            var unverified = $"{DuplicateRejectionPrefix}Package '{packageFileName}' was reported by remote feed '{source}'; remote artifact identity has not been verified.";
            await onLine($"[nuget.push] Unverified duplicate: {unverified}");
            return (false, unverified);
        }

        return (true, null);
    }

    private const string DuplicateRejectionPrefix = "Duplicate on feed: ";
    private const string AuthorizationRejectionPrefix = "API key rejected: ";
    private const string PackageRejectionPrefix = "Package rejected: ";

    private static readonly Regex AuthorizationStatusLine = new(
        @"status code does not indicate success:\s*(?<code>401|403)\b|^\s*(?<word>Unauthorized|Forbidden)\s+https?://",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// True when the feed refused the push because of the API key (HTTP 401/403). Unlike a
    /// timeout or an ambiguous response, this cannot resolve itself while the feed indexes.
    /// </summary>
    internal static bool IsAuthorizationRejection(string? error) =>
        error?.StartsWith(AuthorizationRejectionPrefix, StringComparison.Ordinal) == true;

    private static int? TryParseAuthorizationStatus(string line)
    {
        var match = AuthorizationStatusLine.Match(line);
        if (!match.Success)
            return null;

        if (match.Groups["code"].Success)
            return int.Parse(match.Groups["code"].Value);

        return string.Equals(match.Groups["word"].Value, "Unauthorized", StringComparison.OrdinalIgnoreCase) ? 401 : 403;
    }

    private static readonly Regex PackageRejectionLine = new(
        @"status code does not indicate success:\s*(?<code>4(?!01|03|09)\d\d)\s*\((?<reason>.*)\)\.?\s*$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// True when the feed refused the package itself (an HTTP 4xx other than 401, 403 or 409),
    /// for example because its metadata breaks a feed rule. Pushing the same bytes again can
    /// never succeed: the fix needs a new package, so a new commit.
    /// </summary>
    internal static bool IsPackageRejection(string? error) =>
        error?.StartsWith(PackageRejectionPrefix, StringComparison.Ordinal) == true;

    private static (int Status, string Reason)? TryParsePackageRejection(string line)
    {
        var match = PackageRejectionLine.Match(line);
        return match.Success
            ? (int.Parse(match.Groups["code"].Value), match.Groups["reason"].Value.Trim())
            : null;
    }

    /// <summary>True when the feed refused the push because it already holds this ID/version.</summary>
    internal static bool IsDuplicateRejection(string? error) =>
        error?.StartsWith(DuplicateRejectionPrefix, StringComparison.Ordinal) == true;

    internal static string ComputeFileSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
