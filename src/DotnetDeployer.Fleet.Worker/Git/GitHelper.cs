using System.Diagnostics;

namespace DotnetDeployer.Fleet.WorkerService.Git;

public static class GitHelper
{
    /// <summary>
    /// Clones or updates a repo using the git CLI.
    /// Always fetches full history (required for GitVersion).
    /// Returns the local repo path.
    /// </summary>
    public static async Task CloneOrFetchAsync(
        string gitUrl,
        string branch,
        string localPath,
        Func<string, Task>? log = null,
        CancellationToken ct = default,
        string? gitToken = null,
        string? targetCommitSha = null)
    {
        log ??= _ => Task.CompletedTask;
        var effectiveUrl = InjectToken(gitUrl, gitToken);
        var displayUrl = gitUrl;

        bool isValidRepo = Directory.Exists(Path.Combine(localPath, ".git"));

        if (isValidRepo)
        {
            await log($"Fetching updates for {displayUrl} → {localPath}");

            if (!string.Equals(effectiveUrl, gitUrl, StringComparison.Ordinal))
                await RunGitAsync(["remote", "set-url", "origin", effectiveUrl], localPath, log, ct);

            await RunGitAsync(["fetch", "--all", "--tags", "--recurse-submodules", "--prune"], localPath, log, ct);
            await CheckoutRevisionAndVerifyAsync(localPath, branch, targetCommitSha, log, ct);
        }
        else
        {
            await log($"Cloning {displayUrl} → {localPath}");

            if (Directory.Exists(localPath))
                Directory.Delete(localPath, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            // Full history clone (no --depth) so GitVersion works; recurse submodules
            await RunGitAsync(
                ["clone", "--recurse-submodules", "--branch", branch, effectiveUrl, localPath],
                workingDir: null,
                log, ct);

            await log("Clone complete");

            // Fetch all tags for GitVersion
            await RunGitAsync(["fetch", "--all", "--tags"], localPath, log, ct);
            await CheckoutRevisionAndVerifyAsync(localPath, branch, targetCommitSha, log, ct);
        }
    }

    private static async Task CheckoutRevisionAndVerifyAsync(
        string localPath,
        string branch,
        string? targetCommitSha,
        Func<string, Task> log,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(targetCommitSha))
        {
            try
            {
                await RunGitAsync(["fetch", "origin", targetCommitSha], localPath, log, ct);
            }
            catch
            {
                // Fall back to existing fetched objects if direct fetch fails
            }
            await RunGitAsync(["checkout", "--detach", targetCommitSha], localPath, log, ct);
        }
        else
        {
            await RunGitAsync(["checkout", branch], localPath, log, ct);
            await RunGitAsync(["reset", "--hard", $"origin/{branch}"], localPath, log, ct);
        }

        // Move every submodule HEAD to the commit recorded in the (just-updated) parent
        // tree. `git fetch --recurse-submodules` only downloads objects; it does NOT
        // advance the working tree of the submodules, and neither does `reset --hard`
        // on the parent. Without an explicit `submodule update`, a submodule cloned at
        // commit A stays on A forever, even if the parent now points at B — leading to
        // builds against stale source (e.g. missing APIs added in B).
        // `sync` first in case the submodule URL changed, `--force` to discard any
        // working-tree changes inside the submodule.
        await RunGitAsync(["submodule", "sync", "--recursive"], localPath, log, ct);
        await RunGitAsync(["submodule", "update", "--init", "--recursive", "--force"], localPath, log, ct);

        // Wipe untracked/ignored files (bin/, obj/, generated artifacts, NuGet caches
        // local to the project, etc.) so every job starts from a state equivalent
        // to a fresh clone.
        await RunGitAsync(["clean", "-fdx"], localPath, log, ct);
        await RunGitAsync(["submodule", "foreach", "--recursive", "git clean -fdx"], localPath, log, ct);

        if (!string.IsNullOrWhiteSpace(targetCommitSha))
        {
            var verifiedHead = await GetHeadCommitShaAsync(localPath, ct);
            if (!string.Equals(verifiedHead, targetCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Checked out HEAD '{verifiedHead}' does not match expected immutable commit SHA '{targetCommitSha}'.");
            }
            await log($"Verified immutable revision: {verifiedHead}");
        }
        else
        {
            await log($"Updated to latest {branch}");
        }
    }

    public static async Task<string> GetHeadCommitShaAsync(string localPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = localPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git rev-parse HEAD process");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var outText = await stdout;
        var errText = await stderr;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse HEAD failed (exit {process.ExitCode}): {errText.Trim()}");

        return outText.Trim();
    }

    /// <summary>
    /// Injects a token into an HTTPS git URL so that <c>git</c> can authenticate
    /// against private repositories (GitHub / GitLab / Bitbucket / Azure DevOps).
    /// SSH and other schemes are returned unchanged.
    /// </summary>
    public static string InjectToken(string gitUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return gitUrl;
        if (!Uri.TryCreate(gitUrl, UriKind.Absolute, out var uri)) return gitUrl;
        if (uri.Scheme is not ("http" or "https")) return gitUrl;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return gitUrl;

        var encoded = Uri.EscapeDataString(token);
        var builder = new UriBuilder(uri)
        {
            UserName = "x-access-token",
            Password = encoded
        };
        return builder.Uri.ToString();
    }

    /// <summary>Calculates the total size of a directory in bytes.</summary>
    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return new DirectoryInfo(path)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    private static async Task RunGitAsync(
        string[] args,
        string? workingDir,
        Func<string, Task> log,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? string.Empty
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var outText = await stdout;
        var errText = await stderr;

        foreach (var line in (outText + "\n" + errText).Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await log(line);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {errText.Trim()}");
    }
}
