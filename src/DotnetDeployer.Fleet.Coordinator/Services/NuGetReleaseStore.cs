using System.Security.Cryptography;
using System.Text.Json;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Coordinator.Services;

/// <summary>Coordinator-owned release intent and immutable package bytes, independent of a worker or job attempt.</summary>
public sealed class NuGetReleaseStore(string rootDirectory)
{
    private readonly string root = Path.GetFullPath(rootDirectory);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<NuGetReleaseSnapshot?> GetAsync(Guid projectId, string commitSha, CancellationToken ct = default)
    {
        var dir = DirectoryFor(projectId, commitSha);
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path)) return null;
        var manifest = await ReadAsync<NuGetReleaseManifest>(path, ct);
        var progress = new List<NuGetReleasePackageProgress>();
        foreach (var package in manifest.Packages)
        {
            var progressPath = ProgressPath(dir, package.Id);
            progress.Add(File.Exists(progressPath)
                ? await ReadAsync<NuGetReleasePackageProgress>(progressPath, ct)
                : new(package.Id, NuGetReleasePackageState.Prepared, null, manifest.PreparedAt));
        }
        return new(manifest, progress);
    }

    public async Task UploadPackageAsync(Guid projectId, string commitSha, string packageId, Stream content, CancellationToken ct = default)
    {
        var dir = DirectoryFor(projectId, commitSha);
        var path = PackagePath(dir, packageId);
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(Path.Combine(dir, "manifest.json")))
                throw new InvalidOperationException("Release manifest already exists; package bytes are immutable.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await content.CopyToAsync(output, ct);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }

    public async Task<NuGetReleaseSnapshot> CreateAsync(NuGetReleaseManifest manifest, CancellationToken ct = default)
    {
        var dir = DirectoryFor(manifest.ProjectId, manifest.CommitSha);
        if (manifest.Packages.Count == 0 || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.Source)
            || manifest.Packages.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Packages.Count)
            throw new InvalidOperationException("Release manifest must contain a source, version, and unique packages.");

        await gate.WaitAsync(ct);
        try
        {
            var existing = await GetAsync(manifest.ProjectId, manifest.CommitSha, ct);
            if (existing is not null)
            {
                if (!JsonSerializer.Serialize(existing.Manifest).Equals(JsonSerializer.Serialize(manifest), StringComparison.Ordinal))
                    throw new InvalidOperationException("A different immutable release manifest already exists for this commit.");
                return existing;
            }

            foreach (var package in manifest.Packages)
            {
                if (!string.Equals(package.Version, manifest.Version, StringComparison.OrdinalIgnoreCase)
                    || package.ArtifactPath != $"packages/{package.Id}.nupkg"
                    || string.IsNullOrWhiteSpace(package.ContentHash)
                    || !string.Equals(Hash(PackagePath(dir, package.Id)), package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Staged package '{package.Id}' does not match the manifest.");
            }

            Directory.CreateDirectory(dir);
            await AtomicWriteAsync(Path.Combine(dir, "manifest.json"), manifest, ct);
            return (await GetAsync(manifest.ProjectId, manifest.CommitSha, ct))!;
        }
        finally { gate.Release(); }
    }

    public async Task<NuGetReleaseSnapshot> SetStateAsync(Guid projectId, string commitSha, string packageId,
        NuGetReleasePackageState state, string? detail, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = await GetAsync(projectId, commitSha, ct)
                ?? throw new InvalidOperationException("Release manifest is missing.");
            if (!snapshot.Manifest.Packages.Any(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Package is not in the release manifest.");
            var previous = snapshot.Progress.Single(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));
            if (previous.State == NuGetReleasePackageState.InterventionRequired && state != NuGetReleasePackageState.InterventionRequired)
                throw new InvalidOperationException("Conflict requires manual intervention; automatic retry is forbidden.");
            if (previous.State == NuGetReleasePackageState.Complete
                && state is not (NuGetReleasePackageState.Complete or NuGetReleasePackageState.InterventionRequired))
                throw new InvalidOperationException("A completed package push cannot be marked incomplete.");
            var dir = DirectoryFor(projectId, commitSha);
            await AtomicWriteAsync(ProgressPath(dir, packageId),
                new NuGetReleasePackageProgress(packageId, state, detail, DateTimeOffset.UtcNow), ct);
            return (await GetAsync(projectId, commitSha, ct))!;
        }
        finally { gate.Release(); }
    }

    public FileStream OpenPackage(Guid projectId, string commitSha, string packageId) =>
        File.OpenRead(PackagePath(DirectoryFor(projectId, commitSha), packageId));

    private string DirectoryFor(Guid projectId, string commitSha)
    {
        if (commitSha.Length != 40 || !commitSha.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character Git commit SHA is required.", nameof(commitSha));
        return Path.Combine(root, projectId.ToString("N"), commitSha.ToLowerInvariant());
    }

    private static string PackagePath(string dir, string id) => Path.Combine(dir, "packages", SafeId(id) + ".nupkg");
    private static string ProgressPath(string dir, string id) => Path.Combine(dir, "progress", SafeId(id) + ".json");
    private static string SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("Invalid package ID.", nameof(id));
        return id;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct))
        ?? throw new InvalidDataException($"Invalid release data at {path}.");

    private static async Task AtomicWriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, value, cancellationToken: ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
