using System.Diagnostics;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.WorkerService.Git;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ImmutableRevisionTests : IDisposable
{
    private sealed class InlineFactory(DbContextOptions<FleetDbContext> options)
        : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(options);
    }

    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;
    private readonly List<string> tempDirectories = [];

    public ImmutableRevisionTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-immutable-rev-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new InlineFactory(options);

        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        foreach (var dir in tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456", false)] // 39 chars
    [InlineData("0123456789abcdef0123456789abcdef012345678", false)] // 41 chars
    [InlineData("0123456789abcdef0123456789abcdef0123456g", false)] // non-hex
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsValidFullSha_validates_40_hex_characters(string? sha, bool expected)
    {
        GitCommitResolver.IsValidFullSha(sha).Should().Be(expected);
    }

    [Fact]
    public async Task EnqueueDeploy_with_valid_commit_sha_persists_immutable_TriggerCommitSha()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var resolver = Substitute.For<IGitCommitResolver>();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "proj",
            GitUrl = "https://example.com/repo.git",
            Branch = "main"
        };
        await storage.AddProjectAsync(project);

        const string providedSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var result = await ProjectEndpoints.EnqueueDeploy(
            project.Id,
            new ProjectEndpoints.EnqueueDeployRequest(providedSha),
            storage,
            resolver,
            new JobAssignmentSignal(),
            new DefaultHttpContext());

        result.Should().BeOfType<Created<DeploymentJob>>();
        var job = ((Created<DeploymentJob>)result).Value!;
        job.TriggerCommitSha.Should().Be(providedSha);

        // Verify resolver was NOT called since explicit SHA was provided
        await resolver.DidNotReceiveWithAnyArgs().ResolveLatestShaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task EnqueueDeploy_with_invalid_commit_sha_returns_bad_request()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var resolver = Substitute.For<IGitCommitResolver>();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "proj",
            GitUrl = "https://example.com/repo.git",
            Branch = "main"
        };
        await storage.AddProjectAsync(project);

        var result = await ProjectEndpoints.EnqueueDeploy(
            project.Id,
            new ProjectEndpoints.EnqueueDeployRequest("not-a-valid-sha"),
            storage,
            resolver,
            new JobAssignmentSignal(),
            new DefaultHttpContext());

        var badRequest = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        badRequest.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task EnqueueDeploy_without_commit_sha_resolves_and_persists_HEAD_commit()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var resolver = Substitute.For<IGitCommitResolver>();
        const string resolvedSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        resolver.ResolveLatestShaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(resolvedSha);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "proj",
            GitUrl = "https://example.com/repo.git",
            Branch = "main"
        };
        await storage.AddProjectAsync(project);

        var result = await ProjectEndpoints.EnqueueDeploy(
            project.Id,
            new ProjectEndpoints.EnqueueDeployRequest(null),
            storage,
            resolver,
            new JobAssignmentSignal(),
            new DefaultHttpContext());

        result.Should().BeOfType<Created<DeploymentJob>>();
        var job = ((Created<DeploymentJob>)result).Value!;
        job.TriggerCommitSha.Should().Be(resolvedSha);
    }

    [Fact]
    public async Task EnqueueDeploy_when_resolution_fails_fails_closed_with_bad_request()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var resolver = Substitute.For<IGitCommitResolver>();
        resolver.ResolveLatestShaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "proj",
            GitUrl = "https://example.com/repo.git",
            Branch = "main"
        };
        await storage.AddProjectAsync(project);

        var result = await ProjectEndpoints.EnqueueDeploy(
            project.Id,
            new ProjectEndpoints.EnqueueDeployRequest(null),
            storage,
            resolver,
            new JobAssignmentSignal(),
            new DefaultHttpContext());

        var badRequest = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        badRequest.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task EnqueuePackageBuild_without_commit_sha_resolves_and_persists_HEAD()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var resolver = Substitute.For<IGitCommitResolver>();
        const string resolvedSha = "cccccccccccccccccccccccccccccccccccccccc";
        resolver.ResolveLatestShaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(resolvedSha);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "proj",
            GitUrl = "https://example.com/repo.git",
            Branch = "main"
        };
        await storage.AddProjectAsync(project);

        var request = new PackageBuildRequest
        {
            Targets = [new PackageBuildTarget { Format = "exe-setup", Architecture = "x64" }]
        };

        var result = await ProjectEndpoints.EnqueuePackageBuild(
            project.Id,
            request,
            storage,
            resolver,
            new JobAssignmentSignal(),
            new DefaultHttpContext());

        result.Should().BeOfType<Created<DeploymentJob>>();
        var job = ((Created<DeploymentJob>)result).Value!;
        job.TriggerCommitSha.Should().Be(resolvedSha);
    }

    [Fact]
    public async Task GitHelper_CloneOrFetchAsync_checks_out_exact_targetCommitSha_even_when_branch_advanced()
    {
        var originDir = CreateTempDir("git-origin");
        var cloneDir = CreateTempDir("git-clone");

        RunGit(originDir, "init");
        RunGit(originDir, "config user.name test");
        RunGit(originDir, "config user.email test@example.com");

        File.WriteAllText(Path.Combine(originDir, "file.txt"), "v1");
        RunGit(originDir, "add file.txt");
        RunGit(originDir, "commit -m \"commit 1\"");
        var commit1 = RunGit(originDir, "rev-parse HEAD").Trim();

        // Branch advances with commit 2
        File.WriteAllText(Path.Combine(originDir, "file.txt"), "v2");
        RunGit(originDir, "add file.txt");
        RunGit(originDir, "commit -m \"commit 2\"");
        var commit2 = RunGit(originDir, "rev-parse HEAD").Trim();

        commit1.Should().NotBe(commit2);

        // Worker clones with targetCommitSha = commit1
        await GitHelper.CloneOrFetchAsync(
            gitUrl: originDir,
            branch: "master",
            localPath: cloneDir,
            targetCommitSha: commit1);

        var headInClone = RunGit(cloneDir, "rev-parse HEAD").Trim();
        headInClone.Should().Be(commit1, "worker must checkout the immutable TriggerCommitSha, not the latest branch HEAD");

        var fileContent = await File.ReadAllTextAsync(Path.Combine(cloneDir, "file.txt"));
        fileContent.Should().Be("v1");
    }

    [Fact]
    public async Task GitHelper_CloneOrFetchAsync_throws_when_head_does_not_match_targetCommitSha()
    {
        var originDir = CreateTempDir("git-origin-mismatch");
        var cloneDir = CreateTempDir("git-clone-mismatch");

        RunGit(originDir, "init");
        RunGit(originDir, "config user.name test");
        RunGit(originDir, "config user.email test@example.com");

        File.WriteAllText(Path.Combine(originDir, "file.txt"), "v1");
        RunGit(originDir, "add file.txt");
        RunGit(originDir, "commit -m \"commit 1\"");

        // Passing non-existent SHA must throw
        var act = async () => await GitHelper.CloneOrFetchAsync(
            gitUrl: originDir,
            branch: "master",
            localPath: cloneDir,
            targetCommitSha: "0000000000000000000000000000000000000000");

        await act.Should().ThrowAsync<Exception>();
    }

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }

    private static string RunGit(string workingDirectory, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {args} failed ({process.ExitCode}): {stderr}");
        }
        return stdout;
    }
}
