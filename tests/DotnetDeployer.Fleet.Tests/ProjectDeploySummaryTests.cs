using System.Net;
using System.Text.Json;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Coordinator;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectDeploySummaryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"fleet-deploy-summary-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<FleetDbContext> options;
    private readonly EfFleetStorage storage;

    public ProjectDeploySummaryTests()
    {
        options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        using (var db = new FleetDbContext(options))
            db.Database.EnsureCreated();
        storage = new EfFleetStorage(new InlineFactory(options), new CapabilityWorkerSelector());
    }

    [Fact]
    public async Task Finished_deploy_is_summarised_on_its_project_and_survives_clear_builds()
    {
        var project = await AddProjectAsync();
        var job = await AddRunningDeployAsync(project.Id, "abc123");
        await storage.SetJobVersionIfUnsetAsync(job.Id, "1.2.3");
        job = (await storage.GetJobAsync(job.Id))!;

        job.Status = JobStatus.Succeeded;
        job.NuGetVerification = NuGetVerificationStatus.Pending;
        job.MarkFinished(T0);
        await storage.UpdateJobAsync(job);
        await storage.DeleteFinishedJobsAsync(project.Id);

        var summary = (await storage.GetProjectAsync(project.Id))!;
        (await storage.GetJobsByProjectAsync(project.Id)).Should().BeEmpty();
        summary.LastDeployJobId.Should().Be(job.Id);
        summary.LastDeployFinishedAt.Should().Be(T0);
        summary.LastDeployStatus.Should().Be(JobStatus.Succeeded);
        summary.LastDeployVersion.Should().Be("1.2.3");
        summary.LastDeployCommitSha.Should().Be("abc123");
        summary.LastDeployNuGetVerification.Should().Be(NuGetVerificationStatus.Pending);
        summary.LastActivityAt.Should().Be(T0);
    }

    [Fact]
    public async Task Later_nuget_verification_of_the_last_deploy_updates_the_summary()
    {
        var project = await AddProjectAsync();
        var job = await AddRunningDeployAsync(project.Id, "abc123");
        job.Status = JobStatus.Succeeded;
        job.NuGetVerification = NuGetVerificationStatus.Pending;
        job.MarkFinished(T0);
        await storage.UpdateJobAsync(job);

        job.Status = JobStatus.Failed;
        job.NuGetVerification = NuGetVerificationStatus.Failed;
        await storage.UpdateJobAsync(job);

        var summary = (await storage.GetProjectAsync(project.Id))!;
        summary.LastDeployStatus.Should().Be(JobStatus.Failed);
        summary.LastDeployNuGetVerification.Should().Be(NuGetVerificationStatus.Failed);
    }

    [Fact]
    public async Task An_older_deploy_finishing_late_does_not_replace_the_summary_of_a_newer_one()
    {
        var project = await AddProjectAsync();
        var older = await AddRunningDeployAsync(project.Id, "old");
        var newer = await AddRunningDeployAsync(project.Id, "new");

        newer.Status = JobStatus.Succeeded;
        newer.MarkFinished(T0.AddMinutes(10));
        await storage.UpdateJobAsync(newer);
        older.Status = JobStatus.Cancelled;
        older.MarkFinished(T0.AddMinutes(5));
        await storage.UpdateJobAsync(older);

        var summary = (await storage.GetProjectAsync(project.Id))!;
        summary.LastDeployJobId.Should().Be(newer.Id);
        summary.LastDeployCommitSha.Should().Be("new");
        summary.LastDeployStatus.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task Package_builds_count_as_activity_but_not_as_deploys()
    {
        var project = await AddProjectAsync();
        var build = new DeploymentJob { ProjectId = project.Id, Kind = JobKind.PackageBuild, Status = JobStatus.Running };
        await storage.AddJobAsync(build);

        build.Status = JobStatus.Succeeded;
        build.MarkFinished(T0);
        await storage.UpdateJobAsync(build);

        var summary = (await storage.GetProjectAsync(project.Id))!;
        summary.LastActivityAt.Should().Be(T0);
        summary.LastDeployJobId.Should().BeNull();
        summary.LastDeployStatus.Should().BeNull();
    }

    [Fact]
    public async Task Deploys_failed_by_the_coordinator_are_summarised()
    {
        var project = await AddProjectAsync();
        var workerId = Guid.NewGuid();
        var job = new DeploymentJob { ProjectId = project.Id, WorkerId = workerId, Status = JobStatus.Running, TriggerCommitSha = "abc" };
        await storage.AddJobAsync(job);

        await storage.FailJobsForWorkerAsync(workerId, "Worker removed");

        var summary = (await storage.GetProjectAsync(project.Id))!;
        summary.LastDeployJobId.Should().Be(job.Id);
        summary.LastDeployStatus.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task Editing_a_project_from_a_stale_read_keeps_the_summary()
    {
        var project = await AddProjectAsync();
        var stale = (await storage.GetProjectAsync(project.Id))!;
        var job = await AddRunningDeployAsync(project.Id, "abc123");
        job.Status = JobStatus.Succeeded;
        job.MarkFinished(T0);
        await storage.UpdateJobAsync(job);

        stale.Branch = "develop";
        await storage.UpdateProjectAsync(stale);

        var saved = (await storage.GetProjectAsync(project.Id))!;
        saved.Branch.Should().Be("develop");
        saved.LastDeployJobId.Should().Be(job.Id);
        saved.LastActivityAt.Should().Be(T0);
    }

    [Fact]
    public async Task Backfill_summarises_projects_from_the_jobs_that_predate_the_summary()
    {
        var project = await AddProjectAsync();
        await using (var db = new FleetDbContext(options))
        {
            db.DeploymentJobs.AddRange(
                FinishedJob(project.Id, JobKind.Deploy, JobStatus.Failed, T0, "first"),
                FinishedJob(project.Id, JobKind.Deploy, JobStatus.Succeeded, T0.AddHours(1), "second"),
                FinishedJob(project.Id, JobKind.PackageBuild, JobStatus.Succeeded, T0.AddHours(2), null),
                new DeploymentJob { ProjectId = project.Id, Status = JobStatus.Queued });
            await db.SaveChangesAsync();
        }

        await using (var db = new FleetDbContext(options))
            await ProjectDeploySummary.BackfillAsync(db);

        var summary = (await storage.GetProjectAsync(project.Id))!;
        summary.LastDeployCommitSha.Should().Be("second");
        summary.LastDeployStatus.Should().Be(JobStatus.Succeeded);
        summary.LastDeployFinishedAt.Should().Be(T0.AddHours(1));
        summary.LastActivityAt.Should().Be(T0.AddHours(2));
    }

    [Fact]
    public async Task Existing_databases_gain_the_summary_columns()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"fleet-legacy-summary-{Guid.NewGuid():N}.db");
        try
        {
            var legacyOptions = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite($"Data Source={legacyPath}").Options;
            await using var db = new FleetDbContext(legacyOptions);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Projects" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
                    "Name" TEXT NOT NULL
                )
                """);

            await CoordinatorHostBuilder.EnsureProjectDeploySummaryColumnsAsync(db);
            await CoordinatorHostBuilder.EnsureProjectDeploySummaryColumnsAsync(db);

            var columns = await db.Database
                .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('Projects')")
                .ToListAsync();
            columns.Should().Contain([
                "LastDeployJobId", "LastDeployFinishedAt", "LastDeployStatus", "LastDeployVersion",
                "LastDeployCommitSha", "LastDeployNuGetVerification", "LastActivityAt"
            ]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(legacyPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Api_client_reads_the_summary_returned_by_the_coordinator()
    {
        var project = new Project
        {
            Name = "app",
            LastDeployJobId = Guid.NewGuid(),
            LastDeployFinishedAt = T0,
            LastDeployStatus = JobStatus.Failed,
            LastDeployVersion = "2.0.0",
            LastDeployCommitSha = "abc",
            LastDeployNuGetVerification = NuGetVerificationStatus.Verified,
            LastActivityAt = T0
        };
        // Same shape the coordinator's minimal API writes: web defaults, numeric enums.
        var json = JsonSerializer.Serialize(new[] { project }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHandler(json);
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");

        var received = (await client.GetProjectsAsync()).Single();

        received.Should().BeEquivalentTo(project);
    }

    private async Task<Project> AddProjectAsync()
    {
        var project = new Project { Name = "app", GitUrl = "https://example.test/app.git", Branch = "master" };
        await storage.AddProjectAsync(project);
        return project;
    }

    private async Task<DeploymentJob> AddRunningDeployAsync(Guid projectId, string sha)
    {
        var job = new DeploymentJob { ProjectId = projectId, Status = JobStatus.Running, TriggerCommitSha = sha };
        await storage.AddJobAsync(job);
        return job;
    }

    private static DeploymentJob FinishedJob(Guid projectId, JobKind kind, JobStatus status, DateTimeOffset finishedAt, string? sha) =>
        new() { ProjectId = projectId, Kind = kind, Status = status, FinishedAt = finishedAt, TriggerCommitSha = sha };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }

    private sealed class InlineFactory(DbContextOptions<FleetDbContext> options) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(options);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
