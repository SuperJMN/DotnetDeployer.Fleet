namespace DotnetDeployer.Fleet.WorkerService.Execution;

/// <summary>
/// Installs the toolchains a repository needs before the solution gates run a plain
/// <c>dotnet build</c>/<c>dotnet test</c>. Provisioning is delegated to
/// <c>dotnetdeployer prepare</c>, which already installs the Android SDK and a JDK for
/// Android projects during packaging; the toolchain locations it reports are added to the
/// gate's environment. Without this, a fresh worker fails with <c>XA5300</c> (Android SDK
/// not found) before DotnetDeployer ever gets to install it.
/// </summary>
internal static class ToolchainPreparer
{
    internal static async Task<IReadOnlyDictionary<string, string>?> PrepareAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyDictionary<string, string>? envVars,
        IEnumerable<string>? scrubKeys,
        IStreamingProcessRunner processRunner,
        CancellationToken ct)
    {
        var envFile = Path.Combine(Path.GetTempPath(), $"fleet-toolchain-{Guid.NewGuid():N}.env");

        try
        {
            var prepare = DeployerRunner.CreateDotnetProcessStartInfo(
                workingDirectory,
                ["dnx", "dotnetdeployer.tool", "-y", "prepare", "--directory", workingDirectory, "--env-file", envFile],
                envVars,
                scrubKeys);

            var exitCode = await processRunner.RunAsync(prepare, onLine, ct);
            if (exitCode != 0)
            {
                await onLine($"[WARN] dotnetdeployer prepare exited with code {exitCode}; continuing without prepared toolchains.");
                return envVars;
            }

            return WithToolchain(envVars, ReadEnvFile(envFile));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await onLine($"[WARN] dotnetdeployer prepare failed: {ex.Message}; continuing without prepared toolchains.");
            return envVars;
        }
        finally
        {
            try { File.Delete(envFile); } catch { /* best effort */ }
        }
    }

    private static IReadOnlyDictionary<string, string> ReadEnvFile(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        return File.ReadAllLines(path)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
    }

    private static IReadOnlyDictionary<string, string>? WithToolchain(
        IReadOnlyDictionary<string, string>? envVars,
        IReadOnlyDictionary<string, string> toolchain)
    {
        if (toolchain.Count == 0)
            return envVars;

        var merged = envVars is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(envVars);

        foreach (var (key, value) in toolchain)
            merged[key] = value;

        return merged;
    }
}
