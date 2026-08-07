using System.Diagnostics;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

public static class DeployerRunner
{
    /// <summary>
    /// Invokes DotnetDeployer in the given working directory using
    /// <c>dotnet dnx dotnetdeployer.tool -y</c> (.NET 10 on-demand tool runner).
    /// We invoke <c>dotnet dnx</c> (rather than the standalone <c>dnx</c> shim)
    /// because the <c>dnx</c> script is a thin wrapper around <c>dotnet dnx</c>
    /// that isn't always present (e.g. runtime-only installs, older .NET 10 previews,
    /// or service environments where it isn't on <c>PATH</c>).
    /// Captures stdout/stderr line by line via <paramref name="onLine"/>.
    /// Lines matching the <c>##deployer[phase.*]</c> protocol are parsed and routed
    /// to <paramref name="onPhase"/> instead (and NOT forwarded to <paramref name="onLine"/>).
    /// Injects <paramref name="envVars"/> as extra environment variables for the child process.
    /// Returns true on exit code 0.
    /// </summary>
    public static async Task<(bool Success, string? Error)> RunAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? envVars = null,
        Func<PhaseEvent, Task>? onPhase = null,
        CancellationToken ct = default)
    {
        return await RunWithProcessRunnerAsync(
            workingDirectory,
            onLine,
            arguments,
            envVars,
            onPhase,
            StreamingProcessRunner.Instance,
            ct);
    }

    internal static async Task<(bool Success, string? Error)> RunWithProcessRunnerAsync(
        string workingDirectory,
        Func<string, Task> onLine,
        IReadOnlyList<string>? arguments,
        IReadOnlyDictionary<string, string>? envVars,
        Func<PhaseEvent, Task>? onPhase,
        IStreamingProcessRunner processRunner,
        CancellationToken ct = default)
    {
        var commandArguments = new List<string> { "dnx", "dotnetdeployer.tool", "-y" };
        if (arguments is not null)
            commandArguments.AddRange(arguments);

        var psi = CreateDotnetProcessStartInfo(workingDirectory, commandArguments, envVars);
        var exitCode = await processRunner.RunAsync(psi, async line =>
        {
            // Deployer phase markers are telemetry, not human-readable log lines.
            if (onPhase is not null)
            {
                var ev = PhaseMarkerParser.TryParse(line);
                if (ev is not null)
                {
                    try { await onPhase(ev).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* phase delivery must not tear down the build */ }
                    return;
                }
            }

            await onLine(line).ConfigureAwait(false);
        }, ct);

        if (exitCode != 0)
            return (false, $"dotnetdeployer.tool exited with code {exitCode}");

        return (true, null);
    }

    internal static ProcessStartInfo CreateDotnetProcessStartInfo(
        string workingDirectory,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? envVars = null)
    {
        var psi = new ProcessStartInfo(ResolveDotnetExecutable())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        ApplyBuildEnvironment(psi);

        if (envVars is not null)
        {
            foreach (var (key, value) in envVars)
                psi.Environment[key] = value;
        }

        return psi;
    }

    /// <summary>
    /// Ensures the spawned <c>dotnet dnx</c> process — and every descendant it
    /// spawns (including <c>dotnet publish</c> and any <c>sh</c> launched by
    /// MSBuild <c>&lt;Exec&gt;</c> tasks) — has a usable .NET environment:
    /// <list type="bullet">
    ///   <item><description><c>DOTNET_ROOT</c> pointing to a directory that contains the <c>dotnet</c> host.</description></item>
    ///   <item><description><c>HOME</c> set so per-user tool/NuGet caches resolve correctly under systemd.</description></item>
    ///   <item><description><c>PATH</c> with <c>$DOTNET_ROOT</c> and <c>$HOME/.dotnet/tools</c> prepended,
    ///     so MSBuild <c>&lt;Exec&gt;</c> shell scripts can find <c>dotnet</c>, <c>dnx</c> and global tools.</description></item>
    /// </list>
    /// Without this, builds fail mid-publish with errors like
    /// <c>/usr/bin/sh: ...exec.cmd: dotnet: not found</c> (exit 127) when MSBuild
    /// post-build steps invoke <c>dotnet</c>.
    /// </summary>
    internal static void ApplyBuildEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["UseSharedCompilation"] = "false";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        var separator = OperatingSystem.IsWindows() ? ';' : ':';

        var home = psi.Environment.TryGetValue("HOME", out var existingHome) && !string.IsNullOrEmpty(existingHome)
            ? existingHome
            : Environment.GetEnvironmentVariable("HOME")
              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(home))
            psi.Environment["HOME"] = home;

        var dotnetRoot = ResolveDotnetRoot(psi, home);
        if (!string.IsNullOrEmpty(dotnetRoot))
            psi.Environment["DOTNET_ROOT"] = dotnetRoot;

        var current = psi.Environment.TryGetValue("PATH", out var existing) && !string.IsNullOrEmpty(existing)
            ? existing
            : Environment.GetEnvironmentVariable("PATH") ?? "";

        var parts = current.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();

        void Prepend(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (parts.Contains(dir, StringComparer.Ordinal)) return;
            parts.Insert(0, dir);
        }

        if (!string.IsNullOrEmpty(home))
            Prepend(Path.Combine(home, ".dotnet", "tools"));

        if (!string.IsNullOrEmpty(dotnetRoot))
            Prepend(dotnetRoot);

        psi.Environment["PATH"] = string.Join(separator, parts);
    }

    /// <summary>
    /// Resolves a usable <c>DOTNET_ROOT</c>: existing env var first, then the
    /// running runtime's location (works for self-contained / per-user installs),
    /// then <c>$HOME/.dotnet</c>, then a PATH lookup.
    /// </summary>
    private static string? ResolveDotnetRoot(ProcessStartInfo psi, string? home)
    {
        var env = psi.Environment.TryGetValue("DOTNET_ROOT", out var fromPsi) && !string.IsNullOrEmpty(fromPsi)
            ? fromPsi
            : Environment.GetEnvironmentVariable("DOTNET_ROOT");

        if (!string.IsNullOrEmpty(env) && DotnetHostExists(env))
            return env;

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var candidate = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            if (DotnetHostExists(candidate))
                return candidate;
        }

        if (!string.IsNullOrEmpty(home))
        {
            var userDotnet = Path.Combine(home, ".dotnet");
            if (DotnetHostExists(userDotnet))
                return userDotnet;
        }

        var resolved = ResolveDotnetExecutable();
        if (Path.IsPathRooted(resolved))
        {
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir) && DotnetHostExists(dir))
                return dir;
        }

        return null;
    }

    private static bool DotnetHostExists(string dir)
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return File.Exists(Path.Combine(dir, exe));
    }

    private static string ResolveDotnetExecutable()
    {
        var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        return "dotnet";
    }
}
