using System.Reflection;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotnetDeployer.Fleet.Tests;

public class WorkerListEndpointTests : IDisposable
{
    private readonly string dbPath;
    private readonly EfFleetStorage storage;

    public WorkerListEndpointTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-worker-list-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new InlineFactory(options);

        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        storage = new EfFleetStorage(factory, new CapabilityWorkerSelector());
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
    public async Task A_busy_worker_is_listed_with_the_build_it_is_running()
    {
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "Zafiro", GitUrl = "https://example.com/r.git", Branch = "master"
        });
        await storage.AddWorkerAsync(new Worker { Id = workerId, Name = "pop-os", Status = WorkerStatus.Busy });
        await storage.AddJobAsync(new DeploymentJob
        {
            ProjectId = projectId, WorkerId = workerId, Status = JobStatus.Assigned,
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            Id = runningId, ProjectId = projectId, WorkerId = workerId, Status = JobStatus.Running,
            AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-2), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var worker = (await ListWorkers()).Single();

        worker.CurrentJobId.Should().Be(runningId);
        worker.CurrentJobProjectName.Should().Be("Zafiro");
    }

    [Fact]
    public async Task A_worker_with_only_queued_work_is_not_running_any_build()
    {
        var projectId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        await storage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "Zafiro", GitUrl = "https://example.com/r.git", Branch = "master"
        });
        await storage.AddWorkerAsync(new Worker { Id = workerId, Name = "rpi4", Status = WorkerStatus.Online });
        await storage.AddJobAsync(new DeploymentJob
        {
            ProjectId = projectId, WorkerId = workerId, Status = JobStatus.Assigned, AssignedAt = DateTimeOffset.UtcNow
        });
        await storage.AddJobAsync(new DeploymentJob
        {
            ProjectId = projectId, WorkerId = workerId, Status = JobStatus.Succeeded,
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var worker = (await ListWorkers()).Single();

        worker.CurrentJobId.Should().BeNull();
        worker.CurrentJobProjectName.Should().BeNull();
    }

    private async Task<IReadOnlyList<WorkerEndpoints.WorkerSummary>> ListWorkers()
    {
        var method = typeof(WorkerEndpoints).GetMethod("GetAll", BindingFlags.NonPublic | BindingFlags.Static);
        var result = await (Task<IResult>)method!.Invoke(null, [storage])!;
        return result.Should().BeOfType<Ok<List<WorkerEndpoints.WorkerSummary>>>().Subject.Value!;
    }
}
