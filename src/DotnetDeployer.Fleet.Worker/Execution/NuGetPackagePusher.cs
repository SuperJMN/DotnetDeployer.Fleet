using System.Diagnostics;
using System.Security.Cryptography;

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

        var exitCode = await processRunner.RunAsync(psi, async line =>
        {
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
            if (duplicateConflictDetected)
            {
                var conflictError = $"Conflict: Package '{packageFileName}' already exists in feed '{source}' (HTTP 409 Conflict). Release failed closed to prevent serving unverified or conflicting revision bytes.";
                await onLine($"[nuget.push] CONFLICT: {conflictError}");
                return (false, conflictError);
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
            var conflictError = $"Conflict: Duplicate package '{packageFileName}' was detected on remote feed '{source}' and remote artifact identity could not be verified. Release failed closed.";
            await onLine($"[nuget.push] CONFLICT: {conflictError}");
            return (false, conflictError);
        }

        return (true, null);
    }

    internal static string ComputeFileSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
