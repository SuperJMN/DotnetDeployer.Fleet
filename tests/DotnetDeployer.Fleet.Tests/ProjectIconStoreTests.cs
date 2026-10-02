using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectIconStoreTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"fleet-icons-{Guid.NewGuid():N}");

    public ProjectIconStoreTests()
    {
        Directory.CreateDirectory(tempDir);
    }

    [Fact]
    public async Task ResolveFromCheckout_UsesFirstConfiguredPackageProjectIcon()
    {
        var checkout = CreateCheckout();
        var firstIcon = new byte[] { 1, 2, 3 };
        var secondIcon = new byte[] { 4, 5, 6 };
        WriteProjectWithPackageIcon(checkout, "src/First/First.csproj", "first.png", firstIcon);
        WriteProjectWithPackageIcon(checkout, "src/Second/Second.csproj", "second.png", secondIcon);
        WriteDeployerYaml(checkout, "src/First/First.csproj", "src/Second/Second.csproj");
        var store = CreateStore();

        var icon = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(firstIcon);
        icon.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task ResolveFromCheckout_PrefersPngConventionWhenApplicationIconIsIco()
    {
        var checkout = CreateCheckout();
        var projectDir = Directory.CreateDirectory(Path.Combine(checkout, "src", "App.Desktop")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(projectDir, "icon.ico"), [9, 9, 9]);
        var png = new byte[] { 7, 8, 9 };
        await File.WriteAllBytesAsync(Path.Combine(projectDir, "icon.png"), png);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App.Desktop.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <ApplicationIcon>icon.ico</ApplicationIcon>
              </PropertyGroup>
            </Project>
            """);
        WriteDeployerYaml(checkout, "src/App.Desktop/App.Desktop.csproj");
        var store = CreateStore();

        var icon = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(png);
        icon.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task ResolveFromCheckout_RejectsIconsOutsideCheckout()
    {
        var checkout = CreateCheckout();
        var outsideDir = Directory.CreateDirectory(Path.Combine(tempDir, "outside")).FullName;
        var outsideIcon = Path.Combine(outsideDir, "outside.png");
        await File.WriteAllBytesAsync(outsideIcon, [1, 2, 3]);
        var projectDir = Directory.CreateDirectory(Path.Combine(checkout, "src", "App")).FullName;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageIcon>{{Path.GetRelativePath(projectDir, outsideIcon)}}</PackageIcon>
              </PropertyGroup>
            </Project>
            """);
        WriteDeployerYaml(checkout, "src/App/App.csproj");
        var store = CreateStore();

        var icon = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        icon.Should().BeNull();
    }

    [Fact]
    public async Task GetOrResolve_UsesManualIconBeforeAutomaticCache()
    {
        var projectId = Guid.NewGuid();
        var store = CreateStore();
        await store.Cache(projectId, new ProjectIcon([1, 2, 3], "image/png", ".png"), CancellationToken.None);
        await store.SetManual(projectId, new ProjectIcon([9, 8, 7], "image/png", ".png"), CancellationToken.None);

        var icon = await store.GetOrResolve(new Project { Id = projectId }, CancellationToken.None);

        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(9, 8, 7);
    }

    [Fact]
    public async Task InvalidateAuto_PreservesManualIcon()
    {
        var projectId = Guid.NewGuid();
        var store = CreateStore();
        await store.Cache(projectId, new ProjectIcon([1, 2, 3], "image/png", ".png"), CancellationToken.None);
        await store.SetManual(projectId, new ProjectIcon([9, 8, 7], "image/png", ".png"), CancellationToken.None);

        await store.InvalidateAuto(projectId, CancellationToken.None);

        var icon = await store.TryReadCached(projectId, CancellationToken.None);
        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(9, 8, 7);
    }

    [Fact]
    public async Task ClearManual_FallsBackToAutomaticIcon()
    {
        var projectId = Guid.NewGuid();
        var store = CreateStore();
        await store.Cache(projectId, new ProjectIcon([1, 2, 3], "image/png", ".png"), CancellationToken.None);
        await store.SetManual(projectId, new ProjectIcon([9, 8, 7], "image/png", ".png"), CancellationToken.None);

        await store.ClearManual(projectId, CancellationToken.None);

        var icon = await store.TryReadCached(projectId, CancellationToken.None);
        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void FromBytes_RejectsUnsupportedIcons()
    {
        var icon = ProjectIconStore.FromBytes([1, 2, 3], "icon.gif");

        icon.Should().BeNull();
    }

    [Fact]
    public void FromBytes_RejectsOversizedIcons()
    {
        var icon = ProjectIconStore.FromBytes(new byte[ProjectIconStore.MaxIconBytes + 1], "icon.png");

        icon.Should().BeNull();
    }

    [Fact]
    public async Task ResolveFromCheckout_FallsBackToProjectFilesWhenGithubPackagesAreMissing()
    {
        var checkout = CreateCheckout();
        var icon = new byte[] { 4, 5, 6 };
        await File.WriteAllBytesAsync(Path.Combine(checkout, "icon.png"), icon);
        var projectDir = Directory.CreateDirectory(Path.Combine(checkout, "src", "App")).FullName;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageIcon>icon.png</PackageIcon>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(checkout, "deployer.yaml"), """
            version: 1
            nuget:
              enabled: true
            """);
        var store = CreateStore();

        var resolved = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Bytes.Should().Equal(icon);
        resolved.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task ResolveFromCheckout_PrefersLowercaseAssetsLogoPngOverApplicationIconIco()
    {
        var checkout = CreateCheckout();
        var png = new byte[] { 7, 8, 9 };
        var assetDir = Directory.CreateDirectory(Path.Combine(checkout, "assets")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(assetDir, "logo.png"), png);
        await File.WriteAllBytesAsync(Path.Combine(assetDir, "logo.ico"), [1, 2, 3]);
        var projectDir = Directory.CreateDirectory(Path.Combine(checkout, "src", "App.Desktop")).FullName;
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App.Desktop.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <ApplicationIcon>..\..\assets\logo.ico</ApplicationIcon>
              </PropertyGroup>
            </Project>
            """);
        WriteDeployerYaml(checkout, "src/App.Desktop/App.Desktop.csproj");
        var store = CreateStore();

        var icon = await store.ResolveFromCheckout(new Project(), checkout, CancellationToken.None);

        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(png);
        icon.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Invalidate_RemovesCachedIcon()
    {
        var projectId = Guid.NewGuid();
        var store = CreateStore();
        await store.Cache(projectId, new ProjectIcon([1, 2, 3], "image/png", ".png"), CancellationToken.None);

        await store.Invalidate(projectId, CancellationToken.None);

        var cached = await store.TryReadCached(projectId, CancellationToken.None);
        cached.Should().BeNull();
    }

    [Fact]
    public async Task GetOrResolve_RemembersMissingIconUntilInvalidated()
    {
        var repo = Path.Combine(tempDir, $"repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "no icon yet");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=Fleet", "-c", "user.email=fleet@example.test", "commit", "-m", "initial");
        var project = new Project { Id = Guid.NewGuid(), Name = "NoIcon", GitUrl = repo, Branch = "main" };
        var store = CreateStore();

        (await store.GetOrResolve(project)).Should().BeNull();

        // An icon added later is not picked up by a repeated lookup: the miss is cached, so
        // listing builds no longer clones the repository on every request.
        WriteProjectWithPackageIcon(repo, "src/App/App.csproj", "icon.png", [1, 2, 3]);
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=Fleet", "-c", "user.email=fleet@example.test", "commit", "-m", "icon");
        (await store.GetOrResolve(project)).Should().BeNull();

        // A new commit invalidates the automatic cache (as the poller does).
        await store.InvalidateAuto(project.Id);
        var icon = await store.GetOrResolve(project);
        icon.Should().NotBeNull();
        icon!.Bytes.Should().Equal(1, 2, 3);
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ProjectIconStore CreateStore() =>
        new(Path.Combine(tempDir, "cache"), NullLogger<ProjectIconStore>.Instance);

    private string CreateCheckout()
    {
        var path = Path.Combine(tempDir, $"checkout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private static void WriteProjectWithPackageIcon(string checkout, string projectPath, string iconName, byte[] iconBytes)
    {
        var fullProjectPath = Path.Combine(checkout, projectPath);
        var projectDir = Path.GetDirectoryName(fullProjectPath)!;
        Directory.CreateDirectory(projectDir);
        File.WriteAllBytes(Path.Combine(projectDir, iconName), iconBytes);
        File.WriteAllText(fullProjectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageIcon>{{iconName}}</PackageIcon>
              </PropertyGroup>
            </Project>
            """);
    }

    private static void WriteDeployerYaml(string checkout, params string[] projects)
    {
        var packageLines = string.Join(
            Environment.NewLine,
            projects.Select(project => $"    - project: {project}"));
        File.WriteAllText(Path.Combine(checkout, "deployer.yaml"), $"""
            github:
              packages:
            {packageLines}
            """);
    }
}
