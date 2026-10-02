using System.Collections.Concurrent;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetProjectKit;
using Microsoft.Extensions.Logging;

namespace DotnetDeployer.Fleet.Coordinator.Services;

public sealed record ProjectIcon(byte[] Bytes, string ContentType, string Extension);

public sealed class ProjectIconStore
{
    internal const long MaxIconBytes = 1_000_000;
    private const string NoIconMarker = "none";
    private const string FailedMarker = "failed";
    // A failed clone (network, credentials) is retried after this; "no icon" waits for the next commit.
    internal static readonly TimeSpan FailedLookupRetry = TimeSpan.FromHours(1);
    private readonly string rootDir;
    private readonly ILogger<ProjectIconStore> logger;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<ProjectIcon?>>> resolving = new();

    public ProjectIconStore(string rootDir, ILogger<ProjectIconStore> logger)
    {
        this.rootDir = rootDir;
        this.logger = logger;
    }

    public async Task<ProjectIcon?> GetOrResolve(Project project, CancellationToken ct = default)
    {
        var cached = await TryReadCached(project.Id, ct);
        if (cached is not null)
            return cached;
        if (HasFreshMissMarker(project.Id))
            return null;

        // Resolving clones the repository, which can take a minute on small hosts. Concurrent
        // requests for the same project share one resolution instead of cloning in parallel.
        var resolution = resolving.GetOrAdd(project.Id,
            _ => new Lazy<Task<ProjectIcon?>>(() => Resolve(project)));
        try
        {
            return await resolution.Value.WaitAsync(ct);
        }
        finally
        {
            if (resolution.Value.IsCompleted)
                resolving.TryRemove(new KeyValuePair<Guid, Lazy<Task<ProjectIcon?>>>(project.Id, resolution));
        }
    }

    private async Task<ProjectIcon?> Resolve(Project project)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dotnetfleet-project-icons", $"{project.Id:N}-{Guid.NewGuid():N}");
        try
        {
            await ProjectRepositoryCheckout.CloneShallow(project, tempDir, CancellationToken.None);
            var icon = await ResolveFromCheckout(project, tempDir, CancellationToken.None);
            if (icon is null)
            {
                await WriteMissMarker(project.Id, NoIconMarker);
                return null;
            }

            await Cache(project.Id, icon);
            return icon;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Project icon autodetection failed for {ProjectName}", project.Name);
            await WriteMissMarker(project.Id, FailedMarker);
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup. Icon discovery is advisory.
            }
        }
    }

    private bool HasFreshMissMarker(Guid projectId)
    {
        var directory = AutoDirectory(projectId);
        if (File.Exists(Path.Combine(directory, NoIconMarker)))
            return true;

        var failed = new FileInfo(Path.Combine(directory, FailedMarker));
        return failed.Exists && DateTime.UtcNow - failed.LastWriteTimeUtc < FailedLookupRetry;
    }

    private async Task WriteMissMarker(Guid projectId, string marker)
    {
        try
        {
            var directory = AutoDirectory(projectId);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, marker), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record missing icon for project {ProjectId}", projectId);
        }
    }

    internal async Task<ProjectIcon?> ResolveFromCheckout(Project project, string checkoutRoot, CancellationToken ct)
    {
        var configuredProjectFiles = await ProjectIconDiscovery.ConfiguredPackageProjectFiles(checkoutRoot, ct);
        var icon = await ResolveFirstProjectIcon(project, checkoutRoot, configuredProjectFiles, ct);
        if (icon is not null)
            return icon;

        var configured = new HashSet<string>(configuredProjectFiles, StringComparer.OrdinalIgnoreCase);
        var discoveredProjectFiles = ProjectIconDiscovery.DiscoverProjectFiles(checkoutRoot).Where(projectFile => !configured.Contains(projectFile));
        return await ResolveFirstProjectIcon(project, checkoutRoot, discoveredProjectFiles, ct);
    }

    private async Task<ProjectIcon?> ResolveFirstProjectIcon(Project project, string checkoutRoot, IEnumerable<string> projectFiles, CancellationToken ct)
    {
        foreach (var projectFile in projectFiles)
        {
            var icon = await ResolveProjectIcon(checkoutRoot, projectFile, ct);
            if (icon is not null)
            {
                logger.LogDebug("Resolved icon for {ProjectName} from {ProjectFile}", project.Name, projectFile);
                return icon;
            }
        }

        return null;
    }

    internal async Task<ProjectIcon?> TryReadCached(Guid projectId, CancellationToken ct = default)
    {
        return await TryReadIcon(ManualDirectory(projectId), ct)
               ?? await TryReadIcon(AutoDirectory(projectId), ct)
               ?? await TryReadIcon(ProjectDirectory(projectId), ct);
    }

    internal async Task Cache(Guid projectId, ProjectIcon icon, CancellationToken ct = default)
    {
        await WriteIcon(AutoDirectory(projectId), icon, ct);
        File.Delete(Path.Combine(AutoDirectory(projectId), NoIconMarker));
        File.Delete(Path.Combine(AutoDirectory(projectId), FailedMarker));

        var legacyDirectory = ProjectDirectory(projectId);
        if (Directory.Exists(legacyDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(legacyDirectory, "icon.*"))
                File.Delete(file);
        }
    }

    internal Task SetManual(Guid projectId, ProjectIcon icon, CancellationToken ct = default)
    {
        return WriteIcon(ManualDirectory(projectId), icon, ct);
    }

    internal Task ClearManual(Guid projectId, CancellationToken ct = default)
    {
        _ = ct;
        DeleteDirectory(ManualDirectory(projectId));
        return Task.CompletedTask;
    }

    public Task InvalidateAuto(Guid projectId, CancellationToken ct = default)
    {
        _ = ct;
        DeleteDirectory(AutoDirectory(projectId));

        var legacyDirectory = ProjectDirectory(projectId);
        if (Directory.Exists(legacyDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(legacyDirectory, "icon.*"))
                File.Delete(file);
        }

        return Task.CompletedTask;
    }

    public Task Invalidate(Guid projectId, CancellationToken ct = default)
    {
        _ = ct;
        DeleteDirectory(ProjectDirectory(projectId));

        return Task.CompletedTask;
    }

    internal static ProjectIcon? FromBytes(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0 || bytes.Length > MaxIconBytes)
            return null;

        var extension = Path.GetExtension(fileName);
        var contentType = ContentTypeFor(extension);
        return contentType is null ? null : new ProjectIcon(bytes, contentType, extension.ToLowerInvariant());
    }

    private async Task<ProjectIcon?> ResolveProjectIcon(string checkoutRoot, string projectPath, CancellationToken ct)
    {
        var metadataResult = new ProjectMetadataReader().Read(new FileInfo(projectPath), Serilog.Core.Logger.None);
        if (metadataResult.IsFailure)
        {
            logger.LogDebug("Unable to read project metadata from {ProjectPath}: {Error}", projectPath, metadataResult.Error);
            return null;
        }

        var resolver = new ProjectAssetResolver();
        var projectFile = new FileInfo(projectPath);
        var resolved = resolver.ResolveIcon(projectFile, metadataResult.Value, IsPreferredUiIcon, Serilog.Core.Logger.None)
            ?? resolver.ResolveIcon(projectFile, metadataResult.Value, IsSupportedUiIcon, Serilog.Core.Logger.None);

        if (resolved is null || !IsInside(checkoutRoot, resolved.Path))
            return null;

        var info = new FileInfo(resolved.Path);
        if (!info.Exists || info.Length > MaxIconBytes)
            return null;

        var extension = Path.GetExtension(info.FullName);
        var contentType = ContentTypeFor(extension);
        if (contentType is null)
            return null;

        return new ProjectIcon(await File.ReadAllBytesAsync(info.FullName, ct), contentType, extension.ToLowerInvariant());
    }

    private static async Task<ProjectIcon?> TryReadIcon(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return null;

        var file = Directory.EnumerateFiles(directory, "icon.*")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (file is null)
            return null;

        var info = new FileInfo(file);
        if (info.Length > MaxIconBytes)
            return null;

        var extension = Path.GetExtension(file);
        var contentType = ContentTypeFor(extension);
        return contentType is null
            ? null
            : new ProjectIcon(await File.ReadAllBytesAsync(file, ct), contentType, extension.ToLowerInvariant());
    }

    private static async Task WriteIcon(string directory, ProjectIcon icon, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);

        foreach (var file in Directory.EnumerateFiles(directory, "icon.*"))
            File.Delete(file);

        await File.WriteAllBytesAsync(Path.Combine(directory, "icon" + icon.Extension), icon.Bytes, ct);
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static bool IsInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative == "."
               || (!relative.StartsWith("..", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative));
    }

    private static bool IsPreferredUiIcon(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedUiIcon(string path)
    {
        return IsPreferredUiIcon(path)
               || string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ContentTypeFor(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            _ => null
        };
    }

    private string ProjectDirectory(Guid projectId) =>
        Path.Combine(rootDir, projectId.ToString("N"));

    private string ManualDirectory(Guid projectId) =>
        Path.Combine(ProjectDirectory(projectId), "manual");

    private string AutoDirectory(Guid projectId) =>
        Path.Combine(ProjectDirectory(projectId), "auto");
}
