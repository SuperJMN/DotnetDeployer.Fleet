using DotnetDeployer.Fleet.WorkerService.RepoStorage;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class PhysicalPathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"fleet-physical-path-{Guid.NewGuid():N}");

    public PhysicalPathTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void Directory_under_a_symbolic_link_resolves_to_its_physical_path()
    {
        var physical = Directory.CreateDirectory(Path.Combine(root, "disk", "fleet")).FullName;
        var link = Path.Combine(root, "home-fleet");
        Directory.CreateSymbolicLink(link, physical);
        var repos = Directory.CreateDirectory(Path.Combine(link, "worker", "fleet-repos")).FullName;

        PhysicalPath.Of(repos).Should().Be(Path.Combine(PhysicalPath.Of(physical), "worker", "fleet-repos"));
    }

    [Fact]
    public void Missing_directory_keeps_its_full_path()
    {
        var missing = Path.Combine(root, "missing", "..", "missing");

        PhysicalPath.Of(missing).Should().Be(Path.GetFullPath(missing));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}
