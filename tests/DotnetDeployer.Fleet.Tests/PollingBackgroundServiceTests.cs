using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.Coordinator.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Diagnostics;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Tests for the commit-detection and auto-enqueue logic inside PollingBackgroundService.
/// We test the internal helper by subclassing and exposing it.
/// </summary>
public class PollingBackgroundServiceTests
{
    // Expose protected internals via thin subclass
    private sealed class TestablePoller : PollingBackgroundService
    {
        public TestablePoller(IServiceScopeFactory scopeFactory, ProjectIconStore? icons = null)
            : base(
                scopeFactory,
                NullLogger<PollingBackgroundService>.Instance,
                new JobAssignmentSignal(),
                icons ?? new ProjectIconStore(Path.Combine(Path.GetTempPath(), $"fleet-icons-{Guid.NewGuid():N}"), NullLogger<ProjectIconStore>.Instance))
        { }

        public Task PollAllAsync(CancellationToken ct) => PollAllProjectsAsync(ct);
    }

    private static (TestablePoller poller, IFleetStorage storage) Build(IEnumerable<Project> projects, ProjectIconStore? icons = null)
    {
        var storage = Substitute.For<IFleetStorage>();
        storage.GetProjectsAsync().ReturnsForAnyArgs(projects.ToList());
        storage.AddJobAsync(Arg.Any<DeploymentJob>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        storage.UpdateProjectAsync(Arg.Any<Project>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IFleetStorage)).Returns(storage);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        return (new TestablePoller(factory, icons), storage);
    }

    [Fact]
    public async Task Project_with_zero_polling_interval_is_skipped()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "x",
            GitUrl = "https://example.com/repo.git",
            Branch = "main",
            PollingIntervalMinutes = 0
        };
        var (poller, storage) = Build([project]);

        await poller.PollAllAsync(CancellationToken.None);

        await storage.DidNotReceiveWithAnyArgs().AddJobAsync(default!, default);
    }

    [Fact]
    public async Task Project_polled_too_recently_is_skipped()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "x",
            GitUrl = "https://example.com/repo.git",
            Branch = "main",
            PollingIntervalMinutes = 60,
            // Last polled 5 minutes ago — next poll in 55 min
            LastPolledAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var (poller, storage) = Build([project]);

        await poller.PollAllAsync(CancellationToken.None);

        await storage.DidNotReceiveWithAnyArgs().AddJobAsync(default!, default);
    }

    [Fact]
    public async Task New_commit_preserves_manual_project_icon()
    {
        var repo = CreateGitRepositoryWithCommit();
        var iconRoot = Path.Combine(Path.GetTempPath(), $"fleet-icons-{Guid.NewGuid():N}");
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Pokemon",
            GitUrl = repo,
            Branch = "main",
            PollingIntervalMinutes = 1,
            LastPolledAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastPolledCommitSha = "previous-commit"
        };
        var icons = new ProjectIconStore(iconRoot, NullLogger<ProjectIconStore>.Instance);
        await icons.Cache(project.Id, new ProjectIcon([1, 2, 3], "image/png", ".png"), CancellationToken.None);
        await icons.SetManual(project.Id, new ProjectIcon([9, 8, 7], "image/png", ".png"), CancellationToken.None);
        var (poller, storage) = Build([project], icons);

        try
        {
            await poller.PollAllAsync(CancellationToken.None);

            var icon = await icons.TryReadCached(project.Id, CancellationToken.None);
            icon.Should().NotBeNull();
            icon!.Bytes.Should().Equal(9, 8, 7);
            await storage.Received(1).AddJobAsync(
                Arg.Is<DeploymentJob>(job => job.ProjectId == project.Id && job.IsAutoTriggered),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            DeleteIfExists(repo);
            DeleteIfExists(iconRoot);
        }
    }

    private static string CreateGitRepositoryWithCommit()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"fleet-poll-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);

        RunGit(repo, "init", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "test");
        RunGit(repo, "add", "README.md");
        RunGit(repo, "-c", "user.name=Fleet Tests", "-c", "user.email=fleet@example.test", "commit", "-m", "initial");

        return repo;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{stderr}");
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
