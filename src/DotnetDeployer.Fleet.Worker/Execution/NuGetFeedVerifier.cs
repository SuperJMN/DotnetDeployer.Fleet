using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NuGet.Packaging;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal enum FeedPackageStatus { Missing, Equivalent, Conflict }

internal sealed record PackageIdentity(string Id, string Version, string Sha256, string ContentHash);

/// <summary>Checks exact downloadable ID/version bytes. NuGet content hash excludes an added repository signature.</summary>
internal sealed class NuGetFeedVerifier(HttpClient? client = null)
{
    private readonly HttpClient http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<PackageIdentity> ReadIdentityAsync(string path, CancellationToken ct)
    {
        using var reader = new PackageArchiveReader(path);
        var identity = await reader.GetIdentityAsync(ct);
        var contentHash = reader.GetContentHash(ct);
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new(identity.Id, identity.Version.ToNormalizedString(), sha256, contentHash);
    }

    public async Task<FeedPackageStatus> CheckAsync(string source, PackageIdentity expected, CancellationToken ct)
    {
        var remotePath = await DownloadAsync(source, expected.Id, expected.Version, ct);
        if (remotePath is null) return FeedPackageStatus.Missing;
        try
        {
            var remote = await ReadIdentityAsync(remotePath, ct);
            if (!string.Equals(remote.Id, expected.Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(remote.Version, expected.Version, StringComparison.OrdinalIgnoreCase))
                return FeedPackageStatus.Conflict;
            using var reader = new PackageArchiveReader(remotePath);
            var signed = await reader.IsSignedAsync(ct);
            if (signed && !await VerifySignatureAsync(remotePath, ct)) return FeedPackageStatus.Conflict;
            if (string.Equals(remote.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                return FeedPackageStatus.Equivalent;

            // Repository signing changes the ZIP bytes. NuGet's content hash reconstructs
            // the original archive; only accept that route after a valid trusted signature.
            if (!signed) return FeedPackageStatus.Conflict;
            return string.Equals(remote.ContentHash, expected.ContentHash, StringComparison.Ordinal)
                ? FeedPackageStatus.Equivalent : FeedPackageStatus.Conflict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return FeedPackageStatus.Conflict; }
        finally { File.Delete(remotePath); }
    }

    public async Task<FeedPackageStatus> WaitForExactAsync(string source, PackageIdentity expected,
        TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            var result = await CheckAsync(source, expected, ct);
            if (result != FeedPackageStatus.Missing) return result;
            if (DateTimeOffset.UtcNow >= deadline) return FeedPackageStatus.Missing;
            await Task.Delay(interval, ct);
        } while (true);
    }

    private async Task<string?> DownloadAsync(string source, string id, string version, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "fleet-feed-" + Guid.NewGuid().ToString("N") + ".nupkg");
        if (Directory.Exists(source))
        {
            foreach (var file in Directory.EnumerateFiles(source, "*.nupkg", SearchOption.AllDirectories))
            {
                // Folder feeds may use a flat or hierarchical layout. The nuspec is
                // authoritative, not the filename.
                if (string.Equals(Path.GetFileName(file), $"{id}.{version}.nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, temp);
                    return temp;
                }
                try
                {
                    using var reader = new PackageArchiveReader(file);
                    var found = await reader.GetIdentityAsync(ct);
                    if (!string.Equals(found.Id, id, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(found.Version.ToNormalizedString(), version, StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(file, temp);
                    return temp;
                }
                catch (InvalidDataException) { }
            }
            return null;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var indexUri) || indexUri.Scheme != Uri.UriSchemeHttps && indexUri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("NuGet source must be an existing folder or a v3 HTTP service index.");
        using var indexResponse = await http.GetAsync(indexUri, ct);
        indexResponse.EnsureSuccessStatusCode();
        using var index = JsonDocument.Parse(await indexResponse.Content.ReadAsStringAsync(ct));
        var baseAddress = index.RootElement.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.TryGetProperty("@type", out var type) && type.ToString().Contains("PackageBaseAddress/3.0.0", StringComparison.Ordinal))
            .Select(resource => resource.GetProperty("@id").GetString()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(baseAddress))
            throw new InvalidOperationException("NuGet v3 PackageBaseAddress is unavailable; remote identity cannot be verified.");
        var path = $"{id.ToLowerInvariant()}/{version.ToLowerInvariant()}/{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg";
        using var response = await http.GetAsync(new Uri(new Uri(baseAddress.TrimEnd('/') + "/"), path), HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var output = File.Create(temp);
        await response.Content.CopyToAsync(output, ct);
        return temp;
    }

    private static async Task<bool> VerifySignatureAsync(string packagePath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("nuget");
        process.StartInfo.ArgumentList.Add("verify");
        process.StartInfo.ArgumentList.Add(packagePath);
        process.StartInfo.ArgumentList.Add("--all");
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode == 0;
    }
}
