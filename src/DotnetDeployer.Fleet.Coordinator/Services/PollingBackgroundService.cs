using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DotnetDeployer.Fleet.Coordinator.Services;

/// <summary>
/// Periodically checks each project's branch for new commits.
/// When a new commit SHA is detected, a deployment job is automatically enqueued.
/// </summary>
public class PollingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<PollingBackgroundService> logger;
    private readonly JobAssignmentSignal signal;
    private readonly ProjectIconStore icons;
    private readonly IGitCommitResolver commitResolver;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    public PollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<PollingBackgroundService> logger,
        JobAssignmentSignal signal,
        ProjectIconStore icons,
        IGitCommitResolver? commitResolver = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.signal = signal;
        this.icons = icons;
        this.commitResolver = commitResolver ?? new GitCommitResolver();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllProjectsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in polling loop");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    protected virtual async Task PollAllProjectsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFleetStorage>();

        var projects = await storage.GetProjectsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var project in projects)
        {
            if (project.PollingIntervalMinutes <= 0) continue;

            var nextPoll = project.LastPolledAt?.AddMinutes(project.PollingIntervalMinutes) ?? DateTimeOffset.MinValue;
            if (now < nextPoll) continue;

            await PollProjectAsync(storage, project, ct);
        }
    }

    private async Task PollProjectAsync(IFleetStorage storage, Project project, CancellationToken ct)
    {
        logger.LogDebug("Polling project {Name} ({Branch})", project.Name, project.Branch);

        var latestSha = await commitResolver.ResolveLatestShaAsync(project.GitUrl, project.Branch, project.GitToken, ct);

        project.LastPolledAt = DateTimeOffset.UtcNow;

        if (latestSha is null)
        {
            logger.LogWarning("Could not resolve SHA for {Name}/{Branch}", project.Name, project.Branch);
            await storage.UpdateProjectAsync(project);
            return;
        }

        // First poll: just record the baseline SHA — don't auto-deploy.
        if (project.LastPolledCommitSha is null)
        {
            logger.LogInformation("First poll for {Name}/{Branch}: recording baseline SHA {Sha}", project.Name, project.Branch, latestSha);
            project.LastPolledCommitSha = latestSha;
            await storage.UpdateProjectAsync(project);
            return;
        }

        if (latestSha == project.LastPolledCommitSha)
        {
            await storage.UpdateProjectAsync(project);
            return;
        }

        logger.LogInformation("New commit on {Name}/{Branch}: {Sha} → enqueuing deploy", project.Name, project.Branch, latestSha);

        var job = new DeploymentJob
        {
            ProjectId = project.Id,
            TriggerCommitSha = latestSha,
            IsAutoTriggered = true
        };

        project.LastPolledCommitSha = latestSha;
        await icons.InvalidateAuto(project.Id, ct);

        await storage.AddJobAsync(job, ct);
        await storage.UpdateProjectAsync(project, ct);

        signal.Notify();
    }
}
