using System.Reflection;
using System.Security.Cryptography;
using System.Security.Claims;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DotnetDeployer.Fleet.Tests;

public class JobLifecycleRaceTests : IDisposable
{
    private readonly string dbPath;
    private readonly string releaseRoot;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public JobLifecycleRaceTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-job-race-{Guid.NewGuid():N}.db");
        releaseRoot = Path.Combine(Path.GetTempPath(), $"fleet-job-race-release-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new InlineFactory(options);

        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    private sealed class InlineFactory(DbContextOptions<FleetDbContext> options)
        : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        try { File.Delete(dbPath); } catch { /* best-effort */ }
        try { if (Directory.Exists(releaseRoot)) Directory.Delete(releaseRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ReportStarted_WhenJobIsAlreadyTerminal_ShouldNotMoveItBackToRunning()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await SeedTerminalJob(storage, projectId, workerId, jobId);

        var result = await InvokeReportStarted(jobId, workerId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status409Conflict);
        (await storage.GetJobAsync(jobId))!.Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task ReportCompleted_WhenJobIsAlreadyTerminal_ShouldNotOverwriteStatus()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await SeedTerminalJob(storage, projectId, workerId, jobId);

        var result = await InvokeReportCompleted(jobId, workerId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status409Conflict);
        (await storage.GetJobAsync(jobId))!.Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task ReportCompleted_WhenJobFinishes_ShouldPersistTotalDuration()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var enqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3);

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Running,
            EnqueuedAt = enqueuedAt,
            AssignedAt = enqueuedAt.AddSeconds(10),
            StartedAt = enqueuedAt.AddSeconds(20)
        });

        var result = await InvokeReportCompleted(jobId, workerId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status200OK);
        var completed = await storage.GetJobAsync(jobId);
        completed!.Status.Should().Be(JobStatus.Succeeded);
        completed.FinishedAt.Should().NotBeNull();
        completed.TotalDurationMs.Should().BeGreaterThan(170_000);
    }

    [Fact]
    public async Task ReportCompleted_WhenNuGetIsIndexing_KeepsJobPendingAndReleasesWorker()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var sha = new string('a', 40);
        var releases = new NuGetReleaseStore(releaseRoot);
        var bytes = new byte[] { 1, 2, 3 };
        await releases.UploadPackageAsync(projectId, sha, "Demo", new MemoryStream(bytes));
        await releases.CreateAsync(new NuGetReleaseManifest(projectId, sha, "1.0.0",
            "https://api.nuget.org/v3/index.json", "NUGET_API_KEY", false,
            [new NuGetReleasePackage("Demo", "1.0.0", "packages/Demo.nupkg",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "content-hash")],
            DateTimeOffset.UtcNow));
        await releases.SetStateAsync(projectId, sha, "Demo", NuGetReleasePackageState.AwaitingIndex, "Push outcome uncertain");
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId, WorkerId = workerId,
            Status = JobStatus.Running, TriggerCommitSha = sha,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var result = await InvokeReportCompleted(jobId, workerId, storage,
            new JobEndpoints.CompleteJobRequest(false, null, AwaitingNuGetIndex: true), releases);

        (result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?)
            .Should().Be(StatusCodes.Status200OK);
        var pending = await storage.GetJobAsync(jobId);
        pending!.Status.Should().Be(JobStatus.AwaitingNuGetIndex);
        pending.WorkerId.Should().BeNull();
        pending.FinishedAt.Should().BeNull();
        pending.ErrorMessage.Should().BeNull();
    }

    private static async Task SeedTerminalJob(
        EfFleetStorage storage,
        Guid projectId,
        Guid workerId,
        Guid jobId)
    {
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Cancelled,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
    }

    private static async Task<IResult> InvokeReportStarted(Guid jobId, Guid workerId, EfFleetStorage storage)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "ReportStarted",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task<IResult>)method.Invoke(null, new object[]
        {
            jobId,
            CreateWorkerContext(workerId),
            storage
        })!;
        return await task;
    }

    private static async Task<IResult> InvokeReportCompleted(Guid jobId, Guid workerId, EfFleetStorage storage,
        JobEndpoints.CompleteJobRequest? request = null, NuGetReleaseStore? releases = null)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "ReportCompleted",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var task = (Task<IResult>)method.Invoke(null, new object[]
        {
            jobId,
            request ?? new JobEndpoints.CompleteJobRequest(true, null),
            CreateWorkerContext(workerId),
            storage,
            releases ?? new NuGetReleaseStore(Path.GetTempPath()),
            new LogBroadcaster(),
            new JobAssignmentSignal()
        })!;
        return await task;
    }

    private static HttpContext CreateWorkerContext(Guid workerId) =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("worker_id", workerId.ToString())
            }, "test"))
        };
}
