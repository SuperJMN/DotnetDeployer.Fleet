using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotnetDeployer.Fleet.Coordinator.Services;

public interface IGitCommitResolver
{
    Task<string?> ResolveLatestShaAsync(string gitUrl, string branch, string? gitToken = null, CancellationToken ct = default);
}

public class GitCommitResolver : IGitCommitResolver
{
    private static readonly Regex FullShaRegex = new(@"^[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?$", RegexOptions.Compiled);

    public static bool IsValidFullSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return false;
        return FullShaRegex.IsMatch(sha.Trim());
    }

    public async Task<string?> ResolveLatestShaAsync(string gitUrl, string branch, string? gitToken = null, CancellationToken ct = default)
    {
        try
        {
            var effectiveUrl = InjectToken(gitUrl, gitToken);

            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.ArgumentList.Add("ls-remote");
            psi.ArgumentList.Add("--heads");
            psi.ArgumentList.Add(effectiveUrl);
            psi.ArgumentList.Add($"refs/heads/{branch}");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return null;

            // Output format: "<sha>\trefs/heads/<branch>"
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var sha = line?.Split('\t').FirstOrDefault()?.Trim();
            return IsValidFullSha(sha) ? sha : null;
        }
        catch
        {
            return null;
        }
    }

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
}
