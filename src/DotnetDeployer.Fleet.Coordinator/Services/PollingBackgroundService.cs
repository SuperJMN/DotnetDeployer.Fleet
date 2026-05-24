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
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    public PollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<PollingBackgroundService> logger,
        JobAssignmentSignal signal,
        ProjectIconStore icons)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.signal = signal;
        this.icons = icons;
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

        var latestSha = await GetLatestCommitShaAsync(project.GitUrl, project.Branch, project.GitToken, ct);

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

    private async Task<string?> GetLatestCommitShaAsync(string gitUrl, string branch, string? gitToken, CancellationToken ct)
    {
        try
        {
            var effectiveUrl = InjectToken(gitUrl, gitToken);

            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("ls-remote");
            psi.ArgumentList.Add("--heads");
            psi.ArgumentList.Add(effectiveUrl);
            psi.ArgumentList.Add($"refs/heads/{branch}");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            // Output format: "<sha>\trefs/heads/<branch>"
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return line?.Split('\t').FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "git ls-remote failed for {Url}", gitUrl);
            return null;
        }
    }

    private static string InjectToken(string gitUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return gitUrl;
        if (!Uri.TryCreate(gitUrl, UriKind.Absolute, out var uri)) return gitUrl;
        if (uri.Scheme is not ("http" or "https")) return gitUrl;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return gitUrl;

        var encoded = Uri.EscapeDataString(token);
        var builder = new UriBuilder(uri)
        {
            UserName = "x-access-token",
            Password = encoded
        };
        return builder.Uri.ToString();
    }
}
