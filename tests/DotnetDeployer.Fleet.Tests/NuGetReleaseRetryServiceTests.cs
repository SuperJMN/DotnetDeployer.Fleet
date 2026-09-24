using System.Security.Cryptography;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DotnetDeployer.Fleet.Tests;

public sealed class NuGetReleaseRetryServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fleet-release-retry-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Indexing_wait_requeues_the_same_job_after_more_than_fifteen_minutes()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Demo", GitUrl = "https://example.com/repo.git", Branch = "main" };
        var sha = new string('e', 40);
        var releases = await CreateReleaseAsync(project.Id, sha, now, NuGetReleasePackageState.AwaitingIndex);
        var waiting = new DeploymentJob
        {
            ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.AwaitingNuGetIndex,
            EnqueuedAt = now, WorkerId = null
        };
        var jobs = new List<DeploymentJob> { waiting };
        var (service, _) = CreateService(jobs, project, releases);

        await service.RetryIncompleteAsync(now.AddMinutes(3));
        waiting.Status.Should().Be(JobStatus.AwaitingNuGetIndex);

        await service.RetryIncompleteAsync(now.AddMinutes(16));
        jobs.Should().ContainSingle();
        waiting.Status.Should().Be(JobStatus.Queued);
        waiting.EnqueuedAt.Should().Be(now.AddMinutes(16));
        waiting.InitialEnqueuedAt.Should().Be(now);
        waiting.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Indexing_wait_expires_only_after_the_release_deadline()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Demo", GitUrl = "https://example.com/repo.git", Branch = "main" };
        var sha = new string('f', 40);
        var releases = await CreateReleaseAsync(project.Id, sha, now.AddDays(-2), NuGetReleasePackageState.AwaitingIndex);
        var waiting = new DeploymentJob
        {
            ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.AwaitingNuGetIndex,
            EnqueuedAt = now.AddDays(-2)
        };
        var (service, _) = CreateService([waiting], project, releases);

        await service.RetryIncompleteAsync(now.AddMinutes(5));

        waiting.Status.Should().Be(JobStatus.Failed);
        waiting.ErrorMessage.Should().Contain("24 hours");
        (await releases.GetAsync(project.Id, sha))!.Progress.Single().State
            .Should().Be(NuGetReleasePackageState.Incomplete);
    }

    [Fact]
    public async Task Indexing_deadline_still_permits_one_final_feed_verification()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Demo", GitUrl = "https://example.com/repo.git", Branch = "main" };
        var sha = new string('1', 40);
        var releases = await CreateReleaseAsync(project.Id, sha, now, NuGetReleasePackageState.AwaitingIndex);
        var waiting = new DeploymentJob
        {
            ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.AwaitingNuGetIndex
        };
        var (service, _) = CreateService([waiting], project, releases);

        await service.RetryIncompleteAsync(now.AddHours(25));

        waiting.Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Incomplete_release_retries_after_delay_without_repeating_or_changing_sha()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Demo", GitUrl = "https://example.com/repo.git", Branch = "main" };
        var sha = new string('a', 40);
        var releases = await CreateReleaseAsync(project.Id, sha, now.AddMinutes(-15), NuGetReleasePackageState.AwaitingIndex);
        var failed = new DeploymentJob
        {
            ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.Failed,
            EnqueuedAt = now.AddMinutes(-8), FinishedAt = now.AddMinutes(-5),
            ErrorMessage = "DemoLib is not yet downloadable; release incomplete. Fleet will retry the durable manifest after NuGet indexing."
        };
        var jobs = new List<DeploymentJob> { failed };
        var (service, _) = CreateService(jobs, project, releases);

        await service.RetryIncompleteAsync(now);

        jobs.Should().HaveCount(2);
        jobs[1].Status.Should().Be(JobStatus.Queued);
        jobs[1].IsAutoTriggered.Should().BeTrue();
        jobs[1].TriggerCommitSha.Should().Be(sha);
        var (restarted, _) = CreateService(jobs, project, new NuGetReleaseStore(root));
        await restarted.RetryIncompleteAsync(now.AddMinutes(1));
        jobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Fresh_failure_does_not_retry_and_manual_attempt_takes_precedence()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Demo", GitUrl = "https://example.com/repo.git", Branch = "main" };
        var sha = new string('b', 40);
        var releases = await CreateReleaseAsync(project.Id, sha, now.AddMinutes(-15), NuGetReleasePackageState.AwaitingIndex);
        var failed = new DeploymentJob
        {
            ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.Failed,
            EnqueuedAt = now.AddMinutes(-2), FinishedAt = now.AddMinutes(-1),
            ErrorMessage = "DemoLib is not yet downloadable; release incomplete. Fleet will retry the durable manifest after NuGet indexing."
        };
        var jobs = new List<DeploymentJob> { failed };
        var (service, _) = CreateService(jobs, project, releases);

        await service.RetryIncompleteAsync(now);
        jobs.Should().ContainSingle();

        jobs.Add(new DeploymentJob
        {
            ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.Queued,
            EnqueuedAt = now
        });
        await service.RetryIncompleteAsync(now.AddMinutes(10));
        jobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Historical_incomplete_manifest_is_not_enrolled_in_automatic_retries()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Demo", GitUrl = "https://example.com/repo.git", Branch = "main" };
        var sha = new string('c', 40);
        var releases = await CreateReleaseAsync(project.Id, sha, now.AddMinutes(-15), NuGetReleasePackageState.Incomplete);
        var jobs = new List<DeploymentJob>
        {
            new()
            {
                ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.Failed,
                EnqueuedAt = now.AddMinutes(-8), FinishedAt = now.AddMinutes(-5),
                ErrorMessage = "DemoLib is not yet downloadable; release incomplete. Retry using the durable manifest."
            }
        };
        var (service, _) = CreateService(jobs, project, releases);

        await service.RetryIncompleteAsync(now);

        jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_limit_leaves_manifest_available_for_manual_recovery()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new Project { Id = Guid.NewGuid(), Name = "Demo", GitUrl = "https://example.com/repo.git", Branch = "main" };
        var sha = new string('d', 40);
        var releases = await CreateReleaseAsync(project.Id, sha, now.AddMinutes(-30), NuGetReleasePackageState.AwaitingIndex);
        var jobs = Enumerable.Range(0, 12).Select(i => new DeploymentJob
        {
            ProjectId = project.Id, TriggerCommitSha = sha, Status = JobStatus.Failed,
            EnqueuedAt = now.AddMinutes(-17 + i), FinishedAt = now.AddMinutes(-16 + i),
            ErrorMessage = "DemoLib is not yet downloadable; release incomplete. Fleet will retry the durable manifest after NuGet indexing."
        }).ToList();
        var (service, _) = CreateService(jobs, project, releases);

        await service.RetryIncompleteAsync(now);

        jobs.Should().HaveCount(12);
        jobs[^1].ErrorMessage.Should().Contain("retry the durable manifest manually");
        var release = await releases.GetAsync(project.Id, sha);
        release!.Progress.Single().State.Should().Be(NuGetReleasePackageState.Incomplete);
    }

    private async Task<NuGetReleaseStore> CreateReleaseAsync(Guid projectId, string sha, DateTimeOffset preparedAt,
        NuGetReleasePackageState state)
    {
        var releases = new NuGetReleaseStore(root);
        var bytes = "durable package bytes"u8.ToArray();
        await releases.UploadPackageAsync(projectId, sha, "DemoLib", new MemoryStream(bytes));
        var manifest = new NuGetReleaseManifest(projectId, sha, "1.0.0", "https://api.nuget.org/v3/index.json",
            "NUGET_API_KEY", false,
            [new NuGetReleasePackage("DemoLib", "1.0.0", "packages/DemoLib.nupkg",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "content-hash")], preparedAt);
        await releases.CreateAsync(manifest);
        await releases.SetStateAsync(projectId, sha, "DemoLib", state, "Not indexed");
        return releases;
    }

    private static (NuGetReleaseRetryService Service, IFleetStorage Storage) CreateService(
        List<DeploymentJob> jobs, Project project, NuGetReleaseStore releases)
    {
        var storage = Substitute.For<IFleetStorage>();
        storage.GetJobsAsync(Arg.Any<CancellationToken>()).Returns(_ => jobs.ToList());
        storage.GetJobsByProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(_ => jobs.ToList());
        storage.GetProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);
        storage.AddJobAsync(Arg.Any<DeploymentJob>(), Arg.Any<CancellationToken>())
            .Returns(call => { jobs.Add(call.Arg<DeploymentJob>()); return Task.CompletedTask; });

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IFleetStorage)).Returns(storage);
        scope.ServiceProvider.GetService(typeof(NuGetReleaseStore)).Returns(releases);
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(scope);
        return (new NuGetReleaseRetryService(scopes, new JobAssignmentSignal(),
            NullLogger<NuGetReleaseRetryService>.Instance), storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
