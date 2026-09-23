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

        var psi = DeployerRunner.CreateDotnetProcessStartInfo(
            workingDirectory,
            ["nuget", "push", packagePath, "--api-key", apiKey, "--source", source, "--skip-duplicate"],
            envVars: null,
            scrubKeys: NonNuGetPublishSecrets);

        var duplicateConflictDetected = false;

        var exitCode = await processRunner.RunAsync(psi, async line =>
        {
            if (line.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Conflict", StringComparison.OrdinalIgnoreCase))
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
            return (false, $"dotnet nuget push for '{packageFileName}' exited with code {exitCode}");
        }

        // Post-push verification for local folder feeds:
        if (Directory.Exists(source))
        {
            var pushedCandidates = Directory.GetFiles(source, packageFileName, SearchOption.AllDirectories);
            if (pushedCandidates.Length > 0)
            {
                var feedHash = ComputeFileSha256(pushedCandidates[0]);
                var localHash = ComputeFileSha256(packagePath);
                if (!string.Equals(localHash, feedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"Conflict: Package '{packageFileName}' in feed '{source}' has different contents after push (hash mismatch: local {localHash[..12]} vs feed {feedHash[..12]}).");
                }
            }
        }
        else if (duplicateConflictDetected)
        {
            // For remote feeds, if duplicate was detected via --skip-duplicate,
            // we cannot assume success without verifying remote artifact identity.
            var conflictNotice = $"Warning: Duplicate package '{packageFileName}' was detected on feed '{source}'. Verify feed contents if this was not an idempotent push.";
            await onLine($"[nuget.push] {conflictNotice}");
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
