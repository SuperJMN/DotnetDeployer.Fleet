using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.Coordinator.Services;
using Microsoft.AspNetCore.Mvc;

namespace DotnetDeployer.Fleet.Coordinator.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapGet("/", GetAll);
        group.MapGet("/{id:guid}", GetById);
        group.MapGet("/{id:guid}/icon", GetIcon);
        group.MapPut("/{id:guid}/icon", SetIcon);
        group.MapDelete("/{id:guid}/icon", ResetIcon);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/deploy", EnqueueDeploy);
        group.MapPost("/{id:guid}/packages", EnqueuePackageBuild);
        group.MapGet("/{id:guid}/package-projects", GetPackageProjects);
        group.MapGet("/{id:guid}/jobs", GetJobsForProject);
        group.MapDelete("/{id:guid}/jobs/finished", DeleteFinishedJobs);
    }

    private static async Task<IResult> GetAll(IFleetStorage storage)
    {
        var projects = await storage.GetProjectsAsync();
        return Results.Ok(projects);
    }

    private static async Task<IResult> GetById(Guid id, IFleetStorage storage)
    {
        var project = await storage.GetProjectAsync(id);
        return project is null ? Results.NotFound() : Results.Ok(project);
    }

    private static async Task<IResult> GetIcon(Guid id, IFleetStorage storage, ProjectIconStore icons, HttpContext httpContext)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        var icon = await icons.GetOrResolve(project, httpContext.RequestAborted);
        return icon is null
            ? Results.NotFound()
            : Results.File(icon.Bytes, icon.ContentType);
    }

    private static async Task<IResult> Create([FromBody] CreateProjectRequest req, IFleetStorage storage)
    {
        var project = CreateProject(req);

        await storage.AddProjectAsync(project);
        return Results.Created($"/api/projects/{project.Id}", project);
    }

    private static async Task<IResult> Update(Guid id, [FromBody] UpdateProjectRequest req, IFleetStorage storage, ProjectIconStore icons)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        var oldGitUrl = project.GitUrl;
        var oldBranch = project.Branch;
        var oldGitToken = project.GitToken;

        ApplyUpdate(project, req);

        await storage.UpdateProjectAsync(project);
        if (!string.Equals(oldGitUrl, project.GitUrl, StringComparison.Ordinal)
            || !string.Equals(oldBranch, project.Branch, StringComparison.Ordinal)
            || !string.Equals(oldGitToken, project.GitToken, StringComparison.Ordinal))
        {
            await icons.InvalidateAuto(project.Id);
        }

        return Results.Ok(project);
    }

    private static async Task<IResult> SetIcon(Guid id, IFleetStorage storage, ProjectIconStore icons, HttpContext httpContext)
    {
        var project = await storage.GetProjectAsync(id, httpContext.RequestAborted);
        if (project is null)
            return Results.NotFound();

        if (!httpContext.Request.HasFormContentType)
            return Results.BadRequest(new { error = "Expected multipart/form-data with an icon field." });

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var file = form.Files.GetFile("icon");
        if (file is null)
            return Results.BadRequest(new { error = "Missing icon file." });
        if (file.Length <= 0 || file.Length > ProjectIconStore.MaxIconBytes)
            return Results.BadRequest(new { error = $"Icon must be between 1 byte and {ProjectIconStore.MaxIconBytes} bytes." });

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, httpContext.RequestAborted);
        var icon = ProjectIconStore.FromBytes(buffer.ToArray(), file.FileName);
        if (icon is null)
            return Results.BadRequest(new { error = "Unsupported icon format. Use PNG, JPEG or ICO." });

        await icons.SetManual(id, icon, httpContext.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ResetIcon(Guid id, IFleetStorage storage, ProjectIconStore icons, HttpContext httpContext)
    {
        var project = await storage.GetProjectAsync(id, httpContext.RequestAborted);
        if (project is null)
            return Results.NotFound();

        await icons.ClearManual(id, httpContext.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Delete(Guid id, IFleetStorage storage, ProjectIconStore icons)
    {
        await storage.DeleteProjectAsync(id);
        await icons.Invalidate(id);
        return Results.NoContent();
    }

    private static async Task<IResult> EnqueueDeploy(Guid id, IFleetStorage storage, JobAssignmentSignal signal)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        var job = new DeploymentJob
        {
            ProjectId = id,
            IsAutoTriggered = false
        };

        await storage.AddJobAsync(job);
        signal.Notify();
        return Results.Created($"/api/jobs/{job.Id}", job);
    }

    private static async Task<IResult> EnqueuePackageBuild(
        Guid id,
        [FromBody] PackageBuildRequest req,
        IFleetStorage storage,
        JobAssignmentSignal signal)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        var targets = (req.Targets ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Format) && !string.IsNullOrWhiteSpace(t.Architecture))
            .Select(t => new PackageBuildTarget
            {
                Format = t.Format.Trim(),
                Architecture = t.Architecture.Trim()
            })
            .ToList();

        if (targets.Count == 0)
            return Results.BadRequest(new { error = "At least one package target is required." });

        var packageRequest = new PackageBuildRequest
        {
            PackageProject = string.IsNullOrWhiteSpace(req.PackageProject) ? null : req.PackageProject.Trim(),
            Targets = targets
        };

        var job = new DeploymentJob
        {
            ProjectId = id,
            Kind = JobKind.PackageBuild,
            IsAutoTriggered = false,
            PackageRequestJson = PackageBuildRequest.Serialize(packageRequest)
        };

        await storage.AddJobAsync(job);
        signal.Notify();
        return Results.Created($"/api/jobs/{job.Id}", job);
    }

    private static async Task<IResult> GetPackageProjects(
        Guid id,
        IFleetStorage storage,
        PackageProjectDiscovery discovery,
        HttpContext httpContext)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        try
        {
            var projects = await discovery.DiscoverPackageProjectsAsync(project, httpContext.RequestAborted);
            return Results.Ok(projects);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetJobsForProject(Guid id, IFleetStorage storage)
    {
        var jobs = await storage.GetJobsByProjectAsync(id);
        return Results.Ok(jobs);
    }

    private static async Task<IResult> DeleteFinishedJobs(
        Guid id,
        IFleetStorage storage,
        PackageArtifactStore artifacts)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        var terminal = new[] { JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled };
        var artifactJobs = (await storage.GetJobsByProjectAsync(id))
            .Where(job => job.Kind == JobKind.PackageBuild && terminal.Contains(job.Status))
            .ToList();

        var deleted = await storage.DeleteFinishedJobsAsync(id);

        foreach (var job in artifactJobs)
            await artifacts.DeleteAsync(job);

        return Results.Ok(new { deleted });
    }

    internal static Project CreateProject(CreateProjectRequest req) => new()
    {
        Name = req.Name,
        GitUrl = req.GitUrl,
        Branch = req.Branch,
        PollingIntervalMinutes = req.PollingIntervalMinutes,
        GitToken = string.IsNullOrWhiteSpace(req.GitToken) ? null : req.GitToken,
        RunTestsBeforeDeploy = req.RunTestsBeforeDeploy ?? true
    };

    internal static void ApplyUpdate(Project project, UpdateProjectRequest req)
    {
        project.Name = req.Name ?? project.Name;
        project.GitUrl = req.GitUrl ?? project.GitUrl;
        project.Branch = req.Branch ?? project.Branch;
        if (req.PollingIntervalMinutes.HasValue)
            project.PollingIntervalMinutes = req.PollingIntervalMinutes.Value;
        if (req.GitToken is not null)
            project.GitToken = string.IsNullOrWhiteSpace(req.GitToken) ? null : req.GitToken;
        if (req.RunTestsBeforeDeploy.HasValue)
            project.RunTestsBeforeDeploy = req.RunTestsBeforeDeploy.Value;
    }

    public record CreateProjectRequest(
        string Name,
        string GitUrl,
        string Branch = "main",
        int PollingIntervalMinutes = 0,
        string? GitToken = null,
        bool? RunTestsBeforeDeploy = null);

    public record UpdateProjectRequest(
        string? Name,
        string? GitUrl,
        string? Branch,
        int? PollingIntervalMinutes,
        string? GitToken = null,
        bool? RunTestsBeforeDeploy = null);
}
