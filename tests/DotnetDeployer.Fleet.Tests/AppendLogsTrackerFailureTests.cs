using System.Reflection;
using System.Security.Claims;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Verifies that the <c>AppendLogs</c> handler returns 200 OK even when the
/// best-effort version-detection step throws. Logs are committed *before* the
/// tracker runs, so a tracker failure must not propagate (otherwise the worker
/// retries the batch and we get duplicate log lines). The handler is invoked
/// directly via reflection because it is a private static minimal-API delegate.
/// </summary>
public class AppendLogsTrackerFailureTests : IDisposable
{
    private readonly string dbPath;
    private readonly IDbContextFactory<FleetDbContext> factory;

    public AppendLogsTrackerFailureTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"fleet-resilience-{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// Wraps a real <see cref="EfFleetStorage"/> but makes the version-detection
    /// path throw, leaving the rest of the storage contract intact so logs do get
    /// persisted.
    /// </summary>
    private sealed class ThrowingTrackerStorage(IFleetStorage inner) : IFleetStorage
    {
        public Task<bool> SetJobVersionIfUnsetAsync(Guid jobId, string version, CancellationToken ct = default)
            => throw new InvalidOperationException("boom: simulated tracker DB failure");

        // Pass-through for everything the AppendLogs path actually exercises.
        public Task<DeploymentJob?> GetJobAsync(Guid id, CancellationToken ct = default) => inner.GetJobAsync(id, ct);
        public Task AddLogEntriesAsync(IEnumerable<LogEntry> entries, CancellationToken ct = default) => inner.AddLogEntriesAsync(entries, ct);
        public Task<IReadOnlyList<LogEntry>> GetLogsAsync(Guid jobId, CancellationToken ct = default) => inner.GetLogsAsync(jobId, ct);
        public Task AddProjectAsync(Project project, CancellationToken ct = default) => inner.AddProjectAsync(project, ct);
        public Task AddJobAsync(DeploymentJob job, CancellationToken ct = default) => inner.AddJobAsync(job, ct);

        // Members below are not invoked by the AppendLogs path. Keep them implemented
        // (rather than throwing) so any future refactor doesn't accidentally turn this
        // fake into a fragile mock.
        public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct = default) => inner.GetProjectsAsync(ct);
        public Task<Project?> GetProjectAsync(Guid id, CancellationToken ct = default) => inner.GetProjectAsync(id, ct);
        public Task UpdateProjectAsync(Project project, CancellationToken ct = default) => inner.UpdateProjectAsync(project, ct);
        public Task RecordProjectPollAsync(Guid projectId, DateTimeOffset polledAt, string? commitSha, CancellationToken ct = default) => inner.RecordProjectPollAsync(projectId, polledAt, commitSha, ct);
        public Task DeleteProjectAsync(Guid id, CancellationToken ct = default) => inner.DeleteProjectAsync(id, ct);
        public Task<IReadOnlyList<DeploymentJob>> GetJobsAsync(CancellationToken ct = default) => inner.GetJobsAsync(ct);
        public Task<IReadOnlyList<DeploymentJob>> GetJobsByProjectAsync(Guid projectId, CancellationToken ct = default) => inner.GetJobsByProjectAsync(projectId, ct);
        public Task UpdateJobAsync(DeploymentJob job, CancellationToken ct = default) => inner.UpdateJobAsync(job, ct);
        public Task<int> DeleteFinishedJobsAsync(Guid? projectId, CancellationToken ct = default) => inner.DeleteFinishedJobsAsync(projectId, ct);
        public Task<DeploymentJob?> ClaimNextJobAsync(Guid workerId, CancellationToken ct = default) => inner.ClaimNextJobAsync(workerId, ct);
        public Task<IReadOnlyList<Worker>> GetWorkersAsync(CancellationToken ct = default) => inner.GetWorkersAsync(ct);
        public Task<Worker?> GetWorkerAsync(Guid id, CancellationToken ct = default) => inner.GetWorkerAsync(id, ct);
        public Task AddWorkerAsync(Worker worker, CancellationToken ct = default) => inner.AddWorkerAsync(worker, ct);
        public Task UpdateWorkerAsync(Worker worker, CancellationToken ct = default) => inner.UpdateWorkerAsync(worker, ct);
        public Task<bool> DeleteWorkerAsync(Guid workerId, CancellationToken ct = default) => inner.DeleteWorkerAsync(workerId, ct);
        public Task<bool> TouchWorkerAsync(Guid workerId, CancellationToken ct = default) => inner.TouchWorkerAsync(workerId, ct);
        public Task<IReadOnlyList<RepoCache>> GetRepoCachesAsync(Guid workerId, CancellationToken ct = default) => inner.GetRepoCachesAsync(workerId, ct);
        public Task<RepoCache?> GetRepoCacheAsync(Guid workerId, Guid projectId, CancellationToken ct = default) => inner.GetRepoCacheAsync(workerId, projectId, ct);
        public Task UpsertRepoCacheAsync(RepoCache cache, CancellationToken ct = default) => inner.UpsertRepoCacheAsync(cache, ct);
        public Task DeleteRepoCacheAsync(Guid id, CancellationToken ct = default) => inner.DeleteRepoCacheAsync(id, ct);
        public Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default) => inner.GetUserByUsernameAsync(username, ct);
        public Task<User?> GetUserAsync(Guid id, CancellationToken ct = default) => inner.GetUserAsync(id, ct);
        public Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken ct = default) => inner.GetUsersAsync(ct);
        public Task AddUserAsync(User user, CancellationToken ct = default) => inner.AddUserAsync(user, ct);
        public Task UpdateUserAsync(User user, CancellationToken ct = default) => inner.UpdateUserAsync(user, ct);
        public Task DeleteUserAsync(Guid id, CancellationToken ct = default) => inner.DeleteUserAsync(id, ct);
        public Task<bool> AnyUserExistsAsync(CancellationToken ct = default) => inner.AnyUserExistsAsync(ct);
        public Task<int> MarkOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default) => inner.MarkOfflineWorkersAsync(staleThreshold, ct);
        public Task<IReadOnlyList<Guid>> FailStaleJobsAsync(TimeSpan staleThreshold, CancellationToken ct = default) => inner.FailStaleJobsAsync(staleThreshold, ct);
        public Task<IReadOnlyList<Guid>> FailTimedOutJobsAsync(TimeSpan queuedTimeout, CancellationToken ct = default) => inner.FailTimedOutJobsAsync(queuedTimeout, ct);
        public Task<IReadOnlyList<Guid>> FailStuckAssignedJobsAsync(TimeSpan assignedTimeout, CancellationToken ct = default) => inner.FailStuckAssignedJobsAsync(assignedTimeout, ct);
        public Task<IReadOnlyList<Guid>> FailRunningJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default) => inner.FailRunningJobsForWorkerAsync(workerId, reason, ct);
        public Task<IReadOnlyList<Guid>> FailJobsForWorkerAsync(Guid workerId, string reason, CancellationToken ct = default) => inner.FailJobsForWorkerAsync(workerId, reason, ct);
        public Task<IReadOnlyList<Secret>> GetSecretsAsync(Guid? projectId, CancellationToken ct = default) => inner.GetSecretsAsync(projectId, ct);
        public Task<Secret?> GetSecretAsync(Guid id, CancellationToken ct = default) => inner.GetSecretAsync(id, ct);
        public Task AddSecretAsync(Secret secret, CancellationToken ct = default) => inner.AddSecretAsync(secret, ct);
        public Task UpdateSecretAsync(Secret secret, CancellationToken ct = default) => inner.UpdateSecretAsync(secret, ct);
        public Task DeleteSecretAsync(Guid id, CancellationToken ct = default) => inner.DeleteSecretAsync(id, ct);

        // Push-scheduler additions.
        public Task<IReadOnlyList<DeploymentJob>> GetUnassignedQueuedJobsAsync(CancellationToken ct = default) => inner.GetUnassignedQueuedJobsAsync(ct);
        public Task<IReadOnlyList<DeploymentJob>> GetActiveJobsForWorkerAsync(Guid workerId, CancellationToken ct = default) => inner.GetActiveJobsForWorkerAsync(workerId, ct);
        public Task<bool> AssignJobToWorkerAsync(Guid jobId, Guid workerId, long? estimatedDurationMs, CancellationToken ct = default) => inner.AssignJobToWorkerAsync(jobId, workerId, estimatedDurationMs, ct);
        public Task<DeploymentJob?> GetNextAssignedJobForWorkerAsync(Guid workerId, CancellationToken ct = default) => inner.GetNextAssignedJobForWorkerAsync(workerId, ct);
        public Task<bool> TryStealAssignedJobAsync(Guid jobId, Guid newWorkerId, Guid expectedCurrentWorkerId, long? estimatedDurationMs, CancellationToken ct = default) => inner.TryStealAssignedJobAsync(jobId, newWorkerId, expectedCurrentWorkerId, estimatedDurationMs, ct);
        public Task<IReadOnlyList<Guid>> UnassignJobsOfOfflineWorkersAsync(TimeSpan staleThreshold, CancellationToken ct = default) => inner.UnassignJobsOfOfflineWorkersAsync(staleThreshold, ct);
        public Task<JobDurationStat?> GetJobDurationStatAsync(Guid projectId, Guid workerId, CancellationToken ct = default) => inner.GetJobDurationStatAsync(projectId, workerId, ct);
        public Task<IReadOnlyList<JobDurationStat>> GetJobDurationStatsForProjectAsync(Guid projectId, CancellationToken ct = default) => inner.GetJobDurationStatsForProjectAsync(projectId, ct);
        public Task UpsertJobDurationStatAsync(Guid projectId, Guid workerId, double newEwmaMs, int samples, CancellationToken ct = default) => inner.UpsertJobDurationStatAsync(projectId, workerId, newEwmaMs, samples, ct);

        // Phase tracking additions.
        public Task RecordJobPhaseAsync(Guid jobId, PhaseEvent ev, DateTimeOffset receivedAt, CancellationToken ct = default) => inner.RecordJobPhaseAsync(jobId, ev, receivedAt, ct);
        public Task<IReadOnlyList<JobPhase>> GetJobPhasesAsync(Guid jobId, CancellationToken ct = default) => inner.GetJobPhasesAsync(jobId, ct);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                    owner.Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    [Fact]
    public async Task AppendLogs_returns_Ok_when_tracker_storage_throws_and_logs_warning()
    {
        // Arrange: real storage seeded with a project + a job owned by a known worker.
        var realStorage = new EfFleetStorage(factory, new DotnetDeployer.Fleet.Core.Domain.CapabilityWorkerSelector());
        var workerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await realStorage.AddProjectAsync(new Project
        {
            Id = projectId, Name = "p", GitUrl = "https://example.com/r.git", Branch = "main"
        });
        var jobId = Guid.NewGuid();
        await realStorage.AddJobAsync(new DeploymentJob
        {
            Id = jobId, ProjectId = projectId, WorkerId = workerId,
            Status = JobStatus.Running, EnqueuedAt = DateTimeOffset.UtcNow
        });

        var throwingStorage = new ThrowingTrackerStorage(realStorage);
        var broadcaster = new LogBroadcaster();
        var loggerProvider = new CapturingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("worker_id", workerId.ToString())
            }, "test"))
        };

        // The body carries a parseable GitVersion line so the tracker is *guaranteed*
        // to enter the throwing code path (otherwise it would short-circuit on "no version").
        var req = new JobEndpoints.AppendLogsRequest(new[] { "FullSemVer: 1.0.0" });

        var method = typeof(JobEndpoints).GetMethod(
            "AppendLogs",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        // Act
        var task = (Task<IResult>)method.Invoke(null, new object[]
        {
            jobId, req, httpContext, throwingStorage, broadcaster, loggerFactory
        })!;
        var result = await task;

        // Assert: 200 OK
        var status = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        status.Should().Be(StatusCodes.Status200OK,
            "tracker failures must not surface to the worker — logs are already committed");

        // Logs were persisted regardless of the tracker exception.
        var logs = await realStorage.GetLogsAsync(jobId);
        logs.Should().ContainSingle(l => l.Line == "FullSemVer: 1.0.0");

        // The exception was swallowed *and* observed via a Warning-level log.
        loggerProvider.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException,
            "the swallowed exception must be reported so failures don't go unnoticed");

        loggerFactory.Dispose();
    }
}
