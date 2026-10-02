using System.Reflection;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DotnetDeployer.Fleet.Tests;

public class JobCancellationTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public JobCancellationTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-job-cancel-{Guid.NewGuid():N}.db");
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
    }

    [Fact]
    public async Task CancelJob_WhenJobIsAssigned_ShouldMarkItCancelledImmediately()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId,
            ProjectId = projectId,
            WorkerId = workerId,
            Status = JobStatus.Assigned,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var result = await InvokeCancelJob(jobId, storage);

        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status200OK);

        var cancelled = await storage.GetJobAsync(jobId);
        cancelled!.Status.Should().Be(JobStatus.Cancelled);
        cancelled.CancellationRequestedAt.Should().NotBeNull();
        cancelled.FinishedAt.Should().NotBeNull();
        cancelled.TotalDurationMs.Should().BeGreaterThan(0);
        cancelled.StartedAt.Should().BeNull();
    }

    [Fact]
    public async Task CancelJob_WhenJobIsRunning_ShouldOnlyRequestCancellation()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

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
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            StartedAt = startedAt
        });

        await InvokeCancelJob(jobId, storage);

        var running = await storage.GetJobAsync(jobId);
        running!.Status.Should().Be(JobStatus.Running);
        running.CancellationRequestedAt.Should().NotBeNull();
        running.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task CancelJob_WhileAwaitingNuGetIndex_ShouldFinishImmediately()
    {
        var storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
        var projectId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId,
            Status = JobStatus.AwaitingNuGetIndex,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        await InvokeCancelJob(jobId, storage);

        var cancelled = await storage.GetJobAsync(jobId);
        cancelled!.Status.Should().Be(JobStatus.Cancelled);
        cancelled.FinishedAt.Should().NotBeNull();
    }

    private static async Task<IResult> InvokeCancelJob(Guid jobId, IFleetStorage storage)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "CancelJob",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var broadcaster = new LogBroadcaster();
        var signal = new JobAssignmentSignal();
        var args = method.GetParameters()
            .Select(parameter => ResolveParameter(parameter.ParameterType, jobId, storage, broadcaster, signal))
            .ToArray();

        var task = (Task<IResult>)method.Invoke(null, args)!;
        return await task;
    }

    private static object ResolveParameter(
        Type parameterType,
        Guid jobId,
        IFleetStorage storage,
        LogBroadcaster broadcaster,
        JobAssignmentSignal signal)
    {
        if (parameterType == typeof(Guid)) return jobId;
        if (parameterType == typeof(IFleetStorage)) return storage;
        if (parameterType == typeof(LogBroadcaster)) return broadcaster;
        if (parameterType == typeof(JobAssignmentSignal)) return signal;

        throw new InvalidOperationException($"Unsupported CancelJob parameter type {parameterType}.");
    }
}
