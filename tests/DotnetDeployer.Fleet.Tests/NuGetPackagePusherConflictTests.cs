using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class NuGetPackagePusherConflictTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    public void Dispose()
    {
        foreach (var dir in tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }

    [Fact]
    public async Task Push_detects_conflict_when_feed_contains_different_bytes_and_fails_closed()
    {
        var workDir = CreateTempDir("work");
        var feedDir = CreateTempDir("staging-feed");

        var pkgName = "MyPackage.1.0.0.nupkg";
        var localPkg = Path.Combine(workDir, pkgName);
        var existingFeedPkg = Path.Combine(feedDir, pkgName);

        // Different byte contents
        await File.WriteAllBytesAsync(localPkg, [1, 2, 3, 4, 5]);
        await File.WriteAllBytesAsync(existingFeedPkg, [9, 8, 7, 6, 5]);

        var lines = new List<string>();
        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            localPkg,
            "test-key",
            feedDir,
            line => { lines.Add(line); return Task.CompletedTask; });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Conflict");
        result.Error.Should().Contain("hash mismatch");

        // The existing package in feed must NOT have been overwritten
        var feedBytes = await File.ReadAllBytesAsync(existingFeedPkg);
        feedBytes.Should().Equal([9, 8, 7, 6, 5], "feed content must be preserved without corruption");
    }

    [Fact]
    public async Task Push_detects_conflict_in_hierarchical_feed_when_bytes_differ()
    {
        var workDir = CreateTempDir("work");
        var feedDir = CreateTempDir("hierarchical-feed");

        var pkgName = "MyPackage.1.0.0.nupkg";
        var localPkg = Path.Combine(workDir, pkgName);
        var subDir = Path.Combine(feedDir, "mypackage", "1.0.0");
        Directory.CreateDirectory(subDir);
        var existingFeedPkg = Path.Combine(subDir, pkgName);

        await File.WriteAllBytesAsync(localPkg, [10, 20, 30]);
        await File.WriteAllBytesAsync(existingFeedPkg, [40, 50, 60]);

        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            localPkg,
            "test-key",
            feedDir,
            _ => Task.CompletedTask);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Conflict");
        result.Error.Should().Contain("hash mismatch");
    }

    [Fact]
    public async Task Push_succeeds_as_idempotent_release_when_feed_contains_identical_bytes()
    {
        var workDir = CreateTempDir("work");
        var feedDir = CreateTempDir("staging-feed");

        var pkgName = "MyPackage.1.0.0.nupkg";
        var localPkg = Path.Combine(workDir, pkgName);
        var existingFeedPkg = Path.Combine(feedDir, pkgName);

        // Identical byte contents
        byte[] payload = [11, 22, 33, 44, 55];
        await File.WriteAllBytesAsync(localPkg, payload);
        await File.WriteAllBytesAsync(existingFeedPkg, payload);

        var lines = new List<string>();
        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            localPkg,
            "test-key",
            feedDir,
            line => { lines.Add(line); return Task.CompletedTask; });

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        lines.Should().Contain(l => l.Contains("Idempotent release verified", StringComparison.Ordinal));
    }
}
