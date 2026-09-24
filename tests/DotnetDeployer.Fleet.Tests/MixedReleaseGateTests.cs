using System.Diagnostics;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.WorkerService;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotnetDeployer.Fleet.Tests;

public sealed class MixedReleaseGateTests : IDisposable
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

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fleet-mixed-tests-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    public void DeployerYamlReader_parses_effective_configuration_for_both_destinations(
        bool nugetEnabled,
        bool githubEnabled,
        bool expectedNuget,
        bool expectedGithub)
    {
        var yaml = $"""
            version: 1
            nuget:
              enabled: {nugetEnabled.ToString().ToLowerInvariant()}
              source: https://api.nuget.org/v3/index.json
              apiKey:
                from: env
                name: NUGET_API_KEY
            github:
              enabled: {githubEnabled.ToString().ToLowerInvariant()}
              owner: SuperJMN
              repo: DotnetDeployer.Fleet
              token:
                from: env
                name: GITHUB_TOKEN
            """;

        var config = DeployerYamlReader.ParseConfig(yaml);

        config.NuGet.Enabled.Should().Be(expectedNuget);
        config.GitHub.Enabled.Should().Be(expectedGithub);
    }

    [Fact]
    public void DeployerYamlReader_defaults_to_enabled_when_enabled_flag_is_omitted()
    {
        var yaml = """
            version: 1
            nuget:
              source: https://api.nuget.org/v3/index.json
            github:
              owner: SuperJMN
              repo: DotnetDeployer.Fleet
            """;

        var config = DeployerYamlReader.ParseConfig(yaml);

        config.NuGet.Enabled.Should().BeTrue("omitted enabled in nuget section defaults to true");
        config.GitHub.Enabled.Should().BeTrue("omitted enabled in github section defaults to true");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GitHub_publication_config_disables_only_nuget(bool explicitNuGetEnabled)
    {
        var enabledLine = explicitNuGetEnabled ? "  enabled: true\n" : "";
        var yaml = $"""
            version: 1
            nuget:
            {enabledLine}  source: https://api.nuget.org/v3/index.json
            github:
              enabled: true
              owner: SuperJMN
              repo: RetroSharp
              packages:
                - project: src/RetroSharp.Standalone/RetroSharp.Standalone.csproj
                  formats:
                    - type: deb
                      arch: [x64]
            """;

        var isolated = DeployerYamlReader.DisableNuGetPublishing(yaml);
        var config = DeployerYamlReader.ParseConfig(isolated);

        config.NuGet.Enabled.Should().BeFalse();
        config.GitHub.Enabled.Should().BeTrue();
        isolated.Should().Contain("RetroSharp.Standalone.csproj");
        DeployerYamlReader.ParseConfig(yaml).NuGet.Enabled.Should().BeTrue();
    }

    [Fact]
    public void DeployerYamlReader_detects_single_destination_when_section_is_omitted()
    {
        var nugetOnlyYaml = """
            version: 1
            nuget:
              enabled: true
              source: https://api.nuget.org/v3/index.json
            """;

        var nugetConfig = DeployerYamlReader.ParseConfig(nugetOnlyYaml);
        nugetConfig.NuGet.Enabled.Should().BeTrue();
        nugetConfig.GitHub.Enabled.Should().BeFalse("github section is absent");

        var githubOnlyYaml = """
            version: 1
            github:
              enabled: true
              owner: SuperJMN
            """;

        var githubConfig = DeployerYamlReader.ParseConfig(githubOnlyYaml);
        githubConfig.NuGet.Enabled.Should().BeFalse("nuget section is absent");
        githubConfig.GitHub.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task WorkerDeploymentPipeline_stages_and_verifies_before_publishing_to_both_destinations()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["DemoLib"] };
        var stages = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionBuild: _ => { stages.Add("build"); return Task.FromResult<(bool, string?)>((true, null)); },
            runSolutionTests: _ => { stages.Add("tests"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPack: _ => { stages.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])); },
            verifyInventory: (_, _) => { stages.Add("verify"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPush: (_, _) => { stages.Add("nuget.push"); return Task.FromResult<(bool, string?)>((true, null)); },
            runAdditionalPublish: _ => { stages.Add("github.deploy"); return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeTrue();
        stages.Should().Equal("build", "tests", "pack", "verify", "nuget.push", "github.deploy");
    }

    [Fact]
    public async Task WorkerDeploymentPipeline_rejects_release_without_a_publication_destination()
    {
        var stages = new List<string>();
        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            new DeploymentJob { Kind = JobKind.Deploy },
            new Project { ExpectedPackageIds = ["DemoLib"] },
            runSolutionBuild: _ => { stages.Add("build"); return Task.FromResult<(bool, string?)>((true, null)); },
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((true, null)),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])),
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no enabled publication destination");
        stages.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkerDeploymentPipeline_reports_partial_publication_when_github_fails_after_nuget()
    {
        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            new DeploymentJob { Kind = JobKind.Deploy },
            new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["DemoLib"] },
            runSolutionBuild: _ => Task.FromResult<(bool, string?)>((true, null)),
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((true, null)),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])),
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runPush: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runAdditionalPublish: _ => Task.FromResult<(bool, string?)>((false, "GitHub unavailable")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Partial publication");
        result.Error.Should().Contain("NuGet packages were pushed");
        result.Error.Should().Contain("GitHub unavailable");
    }

    [Fact]
    public async Task WorkerDeploymentPipeline_reports_partial_publication_when_github_throws_after_nuget()
    {
        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            new DeploymentJob { Kind = JobKind.Deploy },
            new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["DemoLib"] },
            runSolutionBuild: _ => Task.FromResult<(bool, string?)>((true, null)),
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((true, null)),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])),
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runPush: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runAdditionalPublish: _ => throw new IOException("Cannot write isolated config"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Partial publication");
        result.Error.Should().Contain("Cannot write isolated config");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerDeploymentPipeline_does_not_publish_when_pack_or_inventory_fails(bool packFails)
    {
        var published = false;
        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            new DeploymentJob { Kind = JobKind.Deploy },
            new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["DemoLib"] },
            runSolutionBuild: _ => Task.FromResult<(bool, string?)>((true, null)),
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((true, null)),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>(
                packFails ? (false, "pack failed", []) : (true, null, ["pkg.nupkg"])),
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((false, "inventory failed")),
            runPush: (_, _) => { published = true; return Task.FromResult<(bool, string?)>((true, null)); },
            runAdditionalPublish: _ => { published = true; return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeFalse();
        published.Should().BeFalse();
    }

    [Fact]
    public async Task WorkerDeploymentPipeline_executes_single_target_nuget()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["DemoLib"] };
        var executedStages = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionBuild: _ => { executedStages.Add("build"); return Task.FromResult<(bool, string?)>((true, null)); },
            runSolutionTests: _ => { executedStages.Add("tests"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPack: _ => { executedStages.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])); },
            verifyInventory: (_, _) => { executedStages.Add("verify"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPush: (_, _) => { executedStages.Add("nuget.push"); return Task.FromResult<(bool, string?)>((true, null)); },
            runAdditionalPublish: null);

        result.Success.Should().BeTrue();
        executedStages.Should().Equal("build", "tests", "pack", "verify", "nuget.push");
    }

    [Fact]
    public async Task WorkerDeploymentPipeline_executes_single_target_github_with_inventory()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["DemoLib"] };
        var executedStages = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionBuild: _ => { executedStages.Add("build"); return Task.FromResult<(bool, string?)>((true, null)); },
            runSolutionTests: _ => { executedStages.Add("tests"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPack: _ => { executedStages.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])); },
            verifyInventory: (_, _) => { executedStages.Add("verify"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPush: null,
            runAdditionalPublish: _ => { executedStages.Add("github.deploy"); return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeTrue();
        executedStages.Should().Equal("build", "tests", "pack", "verify", "github.deploy");
    }

    [Fact]
    public async Task Worker_requires_inventory_before_mixed_release_publication()
    {
        var dbPath = Path.Combine(CreateTempDir("db"), "fleet.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new ReleaseJobsStagingDemonstrationTests.InlineFactory(options);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var artifactStore = new PackageArtifactStore(CreateTempDir("artifacts"));

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            Name = "worker-mixed-test",
            Status = WorkerStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        await storage.AddWorkerAsync(worker);

        var coordinatorClient = new ReleaseJobsStagingDemonstrationTests.DirectStorageWorkerCoordinatorClient(storage);
        var jobSource = new ReleaseJobsStagingDemonstrationTests.DirectStorageWorkerJobSource(storage, artifactStore);

        var workerRepoDir = CreateTempDir("worker-repos");
        var workerService = new RemoteWorkerBackgroundService(
            jobSource,
            coordinatorClient,
            Options.Create(new WorkerOptions { Id = worker.Id, RepoStoragePath = workerRepoDir }),
            NullLogger<RemoteWorkerBackgroundService>.Instance);

        // Create git repo with deployer.yaml having BOTH nuget.enabled: true AND github.enabled: true
        var repoDir = CreateTempDir("mixed-repo");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "deployer.yaml"), """
            version: 1
            github:
              enabled: true
              owner: SuperJMN
              repo: DotnetDeployer.Fleet
              token:
                from: env
                name: GITHUB_TOKEN
            nuget:
              enabled: true
              source: https://api.nuget.org/v3/index.json
              apiKey:
                from: env
                name: NUGET_API_KEY
            """);

        await RunGitCommandAsync(repoDir, "init", "-b", "main");
        await RunGitCommandAsync(repoDir, "config", "user.email", "test@fleet.local");
        await RunGitCommandAsync(repoDir, "config", "user.name", "Fleet Test");
        await RunGitCommandAsync(repoDir, "add", ".");
        await RunGitCommandAsync(repoDir, "commit", "-m", "Initial commit with mixed config");
        var sha = await RunGitCommandAsync(repoDir, "rev-parse", "HEAD");

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "MixedProject",
            GitUrl = repoDir,
            Branch = "main",
            ExpectedPackageIds = []
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = sha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job must be marked Failed
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Failed);
        finishedJob.ErrorMessage.Should().Contain("project.ExpectedPackageIds is empty");

        // Assert: No publish phases were recorded
        var phases = await storage.GetJobPhasesAsync(job.Id);
        var phaseNames = phases.Select(p => p.Name).ToList();
        phaseNames.Should().NotContain("worker.nuget.push");
        phaseNames.Should().NotContain("worker.deployer.github");
        phaseNames.Should().NotContain("worker.deployer.invoke");
    }

    [Fact]
    public async Task Worker_does_not_reject_single_target_nuget_as_mixed()
    {
        var dbPath = Path.Combine(CreateTempDir("db"), "fleet.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new ReleaseJobsStagingDemonstrationTests.InlineFactory(options);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var artifactStore = new PackageArtifactStore(CreateTempDir("artifacts"));

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            Name = "worker-nuget-test",
            Status = WorkerStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        await storage.AddWorkerAsync(worker);

        var coordinatorClient = new ReleaseJobsStagingDemonstrationTests.DirectStorageWorkerCoordinatorClient(storage);
        var jobSource = new ReleaseJobsStagingDemonstrationTests.DirectStorageWorkerJobSource(storage, artifactStore);

        var workerRepoDir = CreateTempDir("worker-repos");
        var workerService = new RemoteWorkerBackgroundService(
            jobSource,
            coordinatorClient,
            Options.Create(new WorkerOptions { Id = worker.Id, RepoStoragePath = workerRepoDir }),
            NullLogger<RemoteWorkerBackgroundService>.Instance);

        // deployer.yaml with nuget enabled, github explicitly disabled
        var repoDir = CreateTempDir("nuget-only-repo");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "deployer.yaml"), """
            version: 1
            github:
              enabled: false
              owner: SuperJMN
              repo: DotnetDeployer.Fleet
            nuget:
              enabled: true
              source: https://api.nuget.org/v3/index.json
            """);

        await RunGitCommandAsync(repoDir, "init", "-b", "main");
        await RunGitCommandAsync(repoDir, "config", "user.email", "test@fleet.local");
        await RunGitCommandAsync(repoDir, "config", "user.name", "Fleet Test");
        await RunGitCommandAsync(repoDir, "add", ".");
        await RunGitCommandAsync(repoDir, "commit", "-m", "NuGet only config");
        var sha = await RunGitCommandAsync(repoDir, "rev-parse", "HEAD");

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "NuGetOnlyProject",
            GitUrl = repoDir,
            Branch = "main",
            ExpectedPackageIds = [] // Will fail on empty expected package inventory, NOT mixed release!
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = sha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job fails due to empty ExpectedPackageIds gate, NOT mixed release rejection!
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.Status.Should().Be(JobStatus.Failed);
        finishedJob.ErrorMessage.Should().NotContain("Mixed release deployment rejected");
        finishedJob.ErrorMessage.Should().Contain("project.ExpectedPackageIds is empty");
    }

    [Fact]
    public async Task Worker_does_not_reject_single_target_github_as_mixed()
    {
        var dbPath = Path.Combine(CreateTempDir("db"), "fleet.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new ReleaseJobsStagingDemonstrationTests.InlineFactory(options);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var artifactStore = new PackageArtifactStore(CreateTempDir("artifacts"));

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            Name = "worker-github-test",
            Status = WorkerStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        await storage.AddWorkerAsync(worker);

        var coordinatorClient = new ReleaseJobsStagingDemonstrationTests.DirectStorageWorkerCoordinatorClient(storage);
        var jobSource = new ReleaseJobsStagingDemonstrationTests.DirectStorageWorkerJobSource(storage, artifactStore);

        var workerRepoDir = CreateTempDir("worker-repos");
        var workerService = new RemoteWorkerBackgroundService(
            jobSource,
            coordinatorClient,
            Options.Create(new WorkerOptions { Id = worker.Id, RepoStoragePath = workerRepoDir }),
            NullLogger<RemoteWorkerBackgroundService>.Instance);

        // deployer.yaml with github enabled, nuget explicitly disabled
        var repoDir = CreateTempDir("github-only-repo");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "deployer.yaml"), """
            version: 1
            github:
              enabled: true
              owner: SuperJMN
              repo: DotnetDeployer.Fleet
            nuget:
              enabled: false
            """);

        await RunGitCommandAsync(repoDir, "init", "-b", "main");
        await RunGitCommandAsync(repoDir, "config", "user.email", "test@fleet.local");
        await RunGitCommandAsync(repoDir, "config", "user.name", "Fleet Test");
        await RunGitCommandAsync(repoDir, "add", ".");
        await RunGitCommandAsync(repoDir, "commit", "-m", "GitHub only config");
        var sha = await RunGitCommandAsync(repoDir, "rev-parse", "HEAD");

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "GitHubOnlyProject",
            GitUrl = repoDir,
            Branch = "main",
            ExpectedPackageIds = []
        };
        await storage.AddProjectAsync(project);

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = JobKind.Deploy,
            TriggerCommitSha = sha,
            Status = JobStatus.Assigned,
            WorkerId = worker.Id,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await storage.AddJobAsync(job);

        // Act
        await workerService.ExecuteJobAsync(job, CancellationToken.None);

        // Assert: Job must NOT be rejected with mixed release rejection error
        var finishedJob = await storage.GetJobAsync(job.Id);
        finishedJob.Should().NotBeNull();
        finishedJob!.ErrorMessage.Should().NotContain("Mixed release deployment rejected");
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
}
