using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.WorkerService;
using DotnetDeployer.Fleet.WorkerService.Coordinator;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// End-to-end integration demonstration tests for release jobs on a staging feed (Issue #29).
/// Exercises complete coordinator + worker execution paths against real Git repositories,
/// real compilation, test execution, DotnetDeployer packaging, inventory validation, and
/// NuGet package publishing to a local staging directory feed.
/// Verifies fail-closed behavior across all gates: build failure, test failure, cancellation,
/// inventory discrepancy, and 409 conflict detection.
/// </summary>
public sealed class ReleaseJobsStagingDemonstrationTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    public void Dispose()
    {
        foreach (var dir in tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            try { if (File.Exists(dir)) File.Delete(dir); } catch { }
        }
    }

    [Fact]
    public async Task Scenario1_HappyPath_releases_exact_inventory_to_staging_feed()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-happy");
        var (storage, _, worker, workerService) = await SetupCoordinatorAndWorkerAsync(stagingFeedDir);
        var (repoDir, commitSha) = await CreateTestRepoAsync("happy", stagingFeedDir, version: "1.0.0", tag: "v1.0.0");

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "HappyProject",
            GitUrl = repoDir,
            Branch = "main",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["DemoLib"]
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = commitSha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act: Worker executes the release job end-to-end
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job status and version in coordinator storage
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Succeeded);
        finishedJob.ErrorMessage.Should().BeNull();
        finishedJob.TriggerCommitSha.Should().Be(commitSha);

        // Assert: All required phases were emitted in order with Ok status
        var phases = await storage.GetJobPhasesAsync(job.Id);
        var phaseNames = phases.Select(p => p.Name).ToList();
        phaseNames.Should().Contain([
            "worker.git.clone",
            "worker.solution.build",
            "worker.solution.test",
            "worker.deployer.pack",
            "worker.inventory.verify",
            "worker.nuget.push"
        ]);

        var buildPhase = phases.First(p => p.Name == "worker.solution.build");
        buildPhase.Status.Should().Be(PhaseStatus.Ok);

        var testPhase = phases.First(p => p.Name == "worker.solution.test");
        testPhase.Status.Should().Be(PhaseStatus.Ok);

        var packPhase = phases.First(p => p.Name == "worker.deployer.pack");
        packPhase.Status.Should().Be(PhaseStatus.Ok);

        var verifyPhase = phases.First(p => p.Name == "worker.inventory.verify");
        verifyPhase.Status.Should().Be(PhaseStatus.Ok);

        var pushPhase = phases.First(p => p.Name == "worker.nuget.push");
        pushPhase.Status.Should().Be(PhaseStatus.Ok);

        // Assert: Logs contain evidence of each step
        var logs = await storage.GetLogsAsync(job.Id);
        var logLines = logs.Select(l => l.Line).ToList();
        logLines.Should().Contain(l => l.Contains("Solution build SUCCEEDED"));
        logLines.Should().Contain(l => l.Contains("Solution tests SUCCEEDED"));
        logLines.Should().Contain(l => l.Contains("Package inventory verified successfully"));
        logLines.Should().Contain(l => l.Contains("All NuGet packages pushed successfully"));

        // Assert: Staging feed contains the published package matching the exact inventory
        var stagedPackages = Directory.GetFiles(stagingFeedDir, "*.nupkg", SearchOption.AllDirectories);
        stagedPackages.Should().HaveCount(1);
        Path.GetFileName(stagedPackages[0]).Should().Be("DemoLib.1.0.0.nupkg");

        var pkgId = NuGetPackageReader.ReadPackageId(stagedPackages[0]);
        pkgId.Should().Be("DemoLib");
    }

    [Fact]
    public async Task Scenario2_MovingBranch_releases_pinned_commit_and_not_advanced_branch_tip()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-moving");
        var (storage, _, worker, workerService) = await SetupCoordinatorAndWorkerAsync(stagingFeedDir);

        // 1. Upstream repo has initial commit C1 (tag v1.0.0, Version 1.0.0)
        var (repoDir, c1Sha) = await CreateTestRepoAsync("moving", stagingFeedDir, version: "1.0.0", tag: "v1.0.0");

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "MovingProject",
            GitUrl = repoDir,
            Branch = "main",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["DemoLib"]
        };
        await storage.AddProjectAsync(project);

        // 2. Release job is enqueued pinned to commit C1
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = c1Sha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // 3. Upstream repo advances to commit C2 (tag v2.0.0, Version 2.0.0) on branch main
        await AdvanceRepoToCommitC2Async(repoDir, version: "2.0.0", tag: "v2.0.0");
        var c2Sha = await RunGitCommandAsync(repoDir, "rev-parse", "HEAD");
        c2Sha.Should().NotBe(c1Sha);

        // 4. Act: Worker executes the job pinned to C1
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job succeeded and recorded C1
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Succeeded);
        finishedJob.TriggerCommitSha.Should().Be(c1Sha);

        // Assert: Staging feed contains the package corresponding to C1 (1.0.0), NOT C2 (2.0.0)
        var stagedPackages = Directory.GetFiles(stagingFeedDir, "*.nupkg", SearchOption.AllDirectories);
        stagedPackages.Should().HaveCount(1);
        Path.GetFileName(stagedPackages[0]).Should().Be("DemoLib.1.0.0.nupkg");

        // Open the package archive and verify the nuspec version is 1.0.0
        using var archive = ZipFile.OpenRead(stagedPackages[0]);
        var nuspecEntry = archive.Entries.First(e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(nuspecEntry.Open());
        var nuspecXml = await reader.ReadToEndAsync();
        nuspecXml.Should().Contain("<version>1.0.0</version>");
        nuspecXml.Should().NotContain("<version>2.0.0</version>");
    }

    [Fact]
    public async Task Scenario3_BuildFailure_halts_pipeline_and_leaves_staging_feed_empty()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-build-fail");
        var (storage, _, worker, workerService) = await SetupCoordinatorAndWorkerAsync(stagingFeedDir);
        var (repoDir, commitSha) = await CreateTestRepoAsync("build-fail", stagingFeedDir, failBuild: true);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "BuildFailProject",
            GitUrl = repoDir,
            Branch = "main",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["DemoLib"]
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = commitSha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job failed
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Failed);
        finishedJob.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        // Assert: Phases show git ok, build fail, and subsequent phases never started
        var phases = await storage.GetJobPhasesAsync(job.Id);
        var buildPhase = phases.FirstOrDefault(p => p.Name == "worker.solution.build");
        buildPhase.Should().NotBeNull();
        buildPhase!.Status.Should().Be(PhaseStatus.Fail);

        phases.Should().NotContain(p => p.Name == "worker.solution.test");
        phases.Should().NotContain(p => p.Name == "worker.deployer.pack");
        phases.Should().NotContain(p => p.Name == "worker.inventory.verify");
        phases.Should().NotContain(p => p.Name == "worker.nuget.push");

        // Staging feed must remain empty
        Directory.GetFiles(stagingFeedDir, "*.nupkg", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario4_TestFailure_halts_pipeline_and_leaves_staging_feed_empty()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-test-fail");
        var (storage, _, worker, workerService) = await SetupCoordinatorAndWorkerAsync(stagingFeedDir);
        var (repoDir, commitSha) = await CreateTestRepoAsync("test-fail", stagingFeedDir, failTest: true);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "TestFailProject",
            GitUrl = repoDir,
            Branch = "main",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["DemoLib"]
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = commitSha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job failed
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Failed);
        finishedJob.ErrorMessage.Should().Contain("dotnet test exited with code 1");

        // Assert: Phases show build ok, test fail, subsequent phases never started
        var phases = await storage.GetJobPhasesAsync(job.Id);
        var buildPhase = phases.FirstOrDefault(p => p.Name == "worker.solution.build");
        buildPhase.Should().NotBeNull();
        buildPhase!.Status.Should().Be(PhaseStatus.Ok);

        var testPhase = phases.FirstOrDefault(p => p.Name == "worker.solution.test");
        testPhase.Should().NotBeNull();
        testPhase!.Status.Should().Be(PhaseStatus.Fail);

        phases.Should().NotContain(p => p.Name == "worker.deployer.pack");
        phases.Should().NotContain(p => p.Name == "worker.inventory.verify");
        phases.Should().NotContain(p => p.Name == "worker.nuget.push");

        // Staging feed must remain empty
        Directory.GetFiles(stagingFeedDir, "*.nupkg", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario5_InventoryDiscrepancy_halts_before_push_and_leaves_staging_feed_empty()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-inv-disc");
        var (storage, _, worker, workerService) = await SetupCoordinatorAndWorkerAsync(stagingFeedDir);
        var (repoDir, commitSha) = await CreateTestRepoAsync("inv-disc", stagingFeedDir, version: "1.0.0", tag: "v1.0.0");

        // Project requires DemoLib AND ExtraLib, but repo only produces DemoLib
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "InvDiscProject",
            GitUrl = repoDir,
            Branch = "main",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["DemoLib", "ExtraLib"]
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = commitSha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job failed at inventory verification
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Failed);
        finishedJob.ErrorMessage.Should().Contain("missing: [ExtraLib]");

        // Assert: Phases show pack ok, verify fail, push never started
        var phases = await storage.GetJobPhasesAsync(job.Id);
        var verifyPhase = phases.FirstOrDefault(p => p.Name == "worker.inventory.verify");
        verifyPhase.Should().NotBeNull();
        verifyPhase!.Status.Should().Be(PhaseStatus.Fail);

        phases.Should().NotContain(p => p.Name == "worker.nuget.push");

        // Staging feed must remain empty
        Directory.GetFiles(stagingFeedDir, "*.nupkg", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario6_Duplicate409Conflict_with_different_bytes_fails_closed_without_overwriting()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-409");
        var (storage, _, worker, workerService) = await SetupCoordinatorAndWorkerAsync(stagingFeedDir);
        var (repoDir, commitSha) = await CreateTestRepoAsync("conflict-409", stagingFeedDir, version: "1.0.0", tag: "v1.0.0");

        // Pre-seed feed with a package having the same name but DIFFERENT byte content
        var existingPkgPath = Path.Combine(stagingFeedDir, "DemoLib.1.0.0.nupkg");
        var conflictingBytes = Encoding.UTF8.GetBytes("Pre-existing conflicting package content with different hash");
        await File.WriteAllBytesAsync(existingPkgPath, conflictingBytes);
        var initialHash = Convert.ToHexString(SHA256.HashData(conflictingBytes));

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Conflict409Project",
            GitUrl = repoDir,
            Branch = "main",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["DemoLib"]
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = commitSha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job failed at push due to conflict
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Failed);
        finishedJob.ErrorMessage.Should().Contain("already exists in feed");
        finishedJob.ErrorMessage.Should().Contain("with different contents");

        var phases = await storage.GetJobPhasesAsync(job.Id);
        var pushPhase = phases.FirstOrDefault(p => p.Name == "worker.nuget.push");
        pushPhase.Should().NotBeNull();
        pushPhase!.Status.Should().Be(PhaseStatus.Fail);

        // Assert: Staging feed file was NOT overwritten and preserves initial conflicting bytes
        File.Exists(existingPkgPath).Should().BeTrue();
        var currentBytes = await File.ReadAllBytesAsync(existingPkgPath);
        var currentHash = Convert.ToHexString(SHA256.HashData(currentBytes));
        currentHash.Should().Be(initialHash);
    }

    [Fact]
    public async Task Scenario7_Cancellation_aborts_pipeline_and_leaves_staging_feed_empty()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-cancel");
        var (storage, _, worker, workerService) = await SetupCoordinatorAndWorkerAsync(stagingFeedDir);
        var (repoDir, commitSha) = await CreateTestRepoAsync("cancel", stagingFeedDir, version: "1.0.0", tag: "v1.0.0");

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "CancelProject",
            GitUrl = repoDir,
            Branch = "main",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["DemoLib"]
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = commitSha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);

        // Act & Assert: Cancellation aborts the pipeline with OperationCanceledException
        var act = async () => await workerService.ExecuteJobAsync(job, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Push was never invoked
        var phases = await storage.GetJobPhasesAsync(job.Id);
        phases.Should().NotContain(p => p.Name == "worker.nuget.push");

        // Staging feed must remain empty
        Directory.GetFiles(stagingFeedDir, "*.nupkg", SearchOption.AllDirectories).Should().BeEmpty();
    }

    #region Helpers & Test Infrastructure

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }

    private async Task<(IFleetStorage Storage, PackageArtifactStore ArtifactStore, Worker Worker, RemoteWorkerBackgroundService WorkerService)>
        SetupCoordinatorAndWorkerAsync(string stagingFeedDir)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"fleet-staging-{Guid.NewGuid():N}.db");
        tempDirectories.Add(dbPath);

        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new InlineFactory(options);

        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var artifactStore = new PackageArtifactStore(CreateTempDir("artifacts"));

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            Name = "staging-worker",
            Status = WorkerStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        await storage.AddWorkerAsync(worker);

        // Register push secret in coordinator storage
        await storage.AddSecretAsync(new Secret
        {
            Id = Guid.NewGuid(),
            Name = "NUGET_API_KEY",
            Value = "staging-secret-key"
        });

        var coordinatorClient = new DirectStorageWorkerCoordinatorClient(storage);
        var jobSource = new DirectStorageWorkerJobSource(storage, artifactStore);

        var workerRepoDir = CreateTempDir("worker-repos");
        var workerOptions = new WorkerOptions
        {
            Id = worker.Id,
            RepoStoragePath = workerRepoDir
        };

        var workerService = new RemoteWorkerBackgroundService(
            jobSource,
            coordinatorClient,
            Options.Create(workerOptions),
            NullLogger<RemoteWorkerBackgroundService>.Instance);

        return (storage, artifactStore, worker, workerService);
    }

    private async Task<(string RepoDir, string CommitSha)> CreateTestRepoAsync(
        string name,
        string stagingFeedDir,
        bool failBuild = false,
        bool failTest = false,
        string version = "1.0.0",
        string tag = "v1.0.0")
    {
        var repoDir = CreateTempDir($"repo-{name}");
        var srcDir = Path.Combine(repoDir, "src", "DemoLib");
        var testDir = Path.Combine(repoDir, "tests", "DemoLibTests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testDir);

        var libCsCode = failBuild
            ? "namespace DemoLib; public class Class1 { invalid C# syntax !!! }"
            : $"namespace DemoLib; public class Class1 {{ public static string Version => \"{version}\"; }}";

        await File.WriteAllTextAsync(Path.Combine(srcDir, "Class1.cs"), libCsCode);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "DemoLib.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <Version>{version}</Version>
              </PropertyGroup>
            </Project>
            """);

        var testCode = failTest
            ? """
              using Xunit;
              namespace DemoLibTests;
              public class DemoTest
              {
                  [Fact]
                  public void FailsOnPurpose() => Assert.Fail("Simulated test failure");
              }
              """
            : $$"""
              using Xunit;
              namespace DemoLibTests;
              public class DemoTest
              {
                  [Fact]
                  public void TestVersion() => Assert.Equal("{{version}}", DemoLib.Class1.Version);
              }
              """;

        await File.WriteAllTextAsync(Path.Combine(testDir, "DemoTest.cs"), testCode);
        await File.WriteAllTextAsync(Path.Combine(testDir, "DemoLibTests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2" />
                <ProjectReference Include="..\..\src\DemoLib\DemoLib.csproj" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(repoDir, "deployer.yaml"), $"""
            version: 1
            nuget:
              enabled: true
              source: {stagingFeedDir}
              apiKey:
                from: env
                name: NUGET_API_KEY
              packages:
                - project: src/DemoLib/DemoLib.csproj
            """);

        await RunDotnetCommandAsync(repoDir, "new", "sln", "-n", "Demo");
        await RunDotnetCommandAsync(repoDir, "sln", "add", "src/DemoLib/DemoLib.csproj", "tests/DemoLibTests/DemoLibTests.csproj");

        await RunGitCommandAsync(repoDir, "init", "-b", "main");
        await RunGitCommandAsync(repoDir, "config", "user.email", "test@fleet.local");
        await RunGitCommandAsync(repoDir, "config", "user.name", "Fleet Test");
        await RunGitCommandAsync(repoDir, "add", ".");
        await RunGitCommandAsync(repoDir, "commit", "-m", $"Release {version}");
        if (!string.IsNullOrEmpty(tag))
        {
            await RunGitCommandAsync(repoDir, "tag", tag);
        }

        var sha = await RunGitCommandAsync(repoDir, "rev-parse", "HEAD");
        return (repoDir, sha);
    }

    private async Task AdvanceRepoToCommitC2Async(string repoDir, string version, string tag)
    {
        var srcDir = Path.Combine(repoDir, "src", "DemoLib");
        var testDir = Path.Combine(repoDir, "tests", "DemoLibTests");

        await File.WriteAllTextAsync(Path.Combine(srcDir, "Class1.cs"),
            $"namespace DemoLib; public class Class1 {{ public static string Version => \"{version}\"; }}");

        await File.WriteAllTextAsync(Path.Combine(srcDir, "DemoLib.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <Version>{version}</Version>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(testDir, "DemoTest.cs"), $$"""
            using Xunit;
            namespace DemoLibTests;
            public class DemoTest
            {
                [Fact]
                public void TestVersion() => Assert.Equal("{{version}}", DemoLib.Class1.Version);
            }
            """);

        await RunGitCommandAsync(repoDir, "add", ".");
        await RunGitCommandAsync(repoDir, "commit", "-m", $"Release {version}");
        if (!string.IsNullOrEmpty(tag))
        {
            await RunGitCommandAsync(repoDir, "tag", tag);
        }
    }

    private static async Task<string> RunGitCommandAsync(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git with args {string.Join(" ", args)}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed (code {process.ExitCode}): {stderr}");

        return stdout.Trim();
    }

    private static async Task RunDotnetCommandAsync(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start dotnet with args {string.Join(" ", args)}");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"dotnet {string.Join(" ", args)} failed (code {process.ExitCode}): {err}");
        }
    }

    internal sealed class InlineFactory(DbContextOptions<FleetDbContext> options) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(options);
    }

    internal sealed class DirectStorageWorkerCoordinatorClient(IFleetStorage storage) : IWorkerCoordinatorClient
    {
        public Task<Worker?> GetSelfAsync(CancellationToken ct = default) => storage.GetWorkerAsync(Guid.Empty, ct);
        public Task SendHeartbeatAsync(Guid workerId, CancellationToken ct = default) => SendHeartbeatAsync(workerId, null, ct);
        public async Task SendHeartbeatAsync(Guid workerId, string? version, CancellationToken ct = default)
        {
            await storage.TouchWorkerAsync(workerId, ct);
            if (version is not null)
            {
                var worker = await storage.GetWorkerAsync(workerId, ct);
                if (worker is not null)
                {
                    worker.Version = version;
                    await storage.UpdateWorkerAsync(worker, ct);
                }
            }
        }
        public async Task UpdateStatusAsync(Guid workerId, WorkerStatus status, CancellationToken ct = default)
        {
            var worker = await storage.GetWorkerAsync(workerId, ct);
            if (worker is not null)
            {
                worker.Status = status;
                await storage.UpdateWorkerAsync(worker, ct);
            }
        }
        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct = default) => storage.GetProjectAsync(projectId, ct);
        public Task<IReadOnlyList<Secret>> GetGlobalSecretsAsync(CancellationToken ct = default) => storage.GetSecretsAsync(null, ct);
        public Task<IReadOnlyList<Secret>> GetProjectSecretsAsync(Guid projectId, CancellationToken ct = default) => storage.GetSecretsAsync(projectId, ct);
        public Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default) => storage.GetRepoCachesAsync(workerId, ct);
        public Task UpsertRepoCacheAsync(Guid workerId, RepoCache cache, CancellationToken ct = default) => storage.UpsertRepoCacheAsync(cache, ct);
        public Task DeleteRepoCacheAsync(Guid workerId, Guid cacheId, CancellationToken ct = default) => storage.DeleteRepoCacheAsync(cacheId, ct);
    }

    internal sealed class DirectStorageWorkerJobSource(IFleetStorage storage, PackageArtifactStore artifactStore) : IWorkerJobSource
    {
        public Task<DeploymentJob?> GetNextJobAsync(Guid workerId, CancellationToken ct = default)
            => storage.GetNextAssignedJobForWorkerAsync(workerId, ct);

        public async Task ReportJobStartedAsync(Guid jobId, Guid workerId, CancellationToken ct = default)
        {
            var job = await storage.GetJobAsync(jobId, CancellationToken.None)
                ?? throw new InvalidOperationException($"Job {jobId} not found");
            job.Status = JobStatus.Running;
            job.WorkerId = workerId;
            job.StartedAt = DateTimeOffset.UtcNow;
            await storage.UpdateJobAsync(job, CancellationToken.None);
        }

        public async Task SendLogChunkAsync(Guid jobId, IEnumerable<string> lines, CancellationToken ct = default)
        {
            var entries = lines.Select(l => new LogEntry
            {
                JobId = jobId,
                Line = l,
                Timestamp = DateTimeOffset.UtcNow
            }).ToList();
            await storage.AddLogEntriesAsync(entries, CancellationToken.None);
        }

        public async Task UploadArtifactAsync(Guid jobId, string relativePath, Stream content, CancellationToken ct = default)
        {
            var job = await storage.GetJobAsync(jobId, CancellationToken.None)
                ?? throw new InvalidOperationException($"Job {jobId} not found");
            await artifactStore.SaveAsync(job, relativePath, content, CancellationToken.None);
        }

        public async Task ReportJobCompletedAsync(Guid jobId, bool success, string? errorMessage, CancellationToken ct = default)
        {
            var job = await storage.GetJobAsync(jobId, CancellationToken.None)
                ?? throw new InvalidOperationException($"Job {jobId} not found");
            job.Status = success ? JobStatus.Succeeded : JobStatus.Failed;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.ErrorMessage = errorMessage;
            await storage.UpdateJobAsync(job, CancellationToken.None);
        }

        public async Task PostJobPhaseAsync(Guid jobId, PhaseEvent ev, CancellationToken ct = default)
        {
            try
            {
                await storage.RecordJobPhaseAsync(jobId, ev, DateTimeOffset.UtcNow, CancellationToken.None);
            }
            catch
            {
                // Telemetry never fails
            }
        }

        public async Task<JobAction> GetJobActionAsync(Guid jobId, CancellationToken ct = default)
        {
            var job = await storage.GetJobAsync(jobId, ct);
            if (job is null) return JobAction.Abort;
            if (job.Status == JobStatus.Cancelled) return JobAction.Cancel;
            return JobAction.Continue;
        }
    }

    #endregion
}
