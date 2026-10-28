using DotnetDeployer.Fleet.Coordinator.Auth;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DotnetDeployer.Fleet.Coordinator.Endpoints;

public static class SecretEndpoints
{
    public static void MapSecretEndpoints(this WebApplication app)
    {
        // Global secrets
        var globals = app.MapGroup("/api/secrets").RequireAuthorization();
        globals.MapGet("/", GetGlobals);
        globals.MapPost("/", CreateGlobal).RequireAuthorization(AccessPolicies.Write);
        globals.MapPut("/{id:guid}", UpdateGlobal).RequireAuthorization(AccessPolicies.Write);
        globals.MapDelete("/{id:guid}", DeleteGlobal).RequireAuthorization(AccessPolicies.Write);

        // Per-project secrets (nested under /api/projects/{projectId}/secrets)
        var perProject = app.MapGroup("/api/projects/{projectId:guid}/secrets").RequireAuthorization();
        perProject.MapGet("/", GetByProject);
        perProject.MapPost("/", CreateForProject).RequireAuthorization(AccessPolicies.Write);
        perProject.MapPut("/{id:guid}", UpdateForProject).RequireAuthorization(AccessPolicies.Write);
        perProject.MapDelete("/{id:guid}", DeleteForProject).RequireAuthorization(AccessPolicies.Write);
    }

    // ── Global ────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetGlobals(IFleetStorage storage, HttpContext httpContext)
    {
        var secrets = await storage.GetSecretsAsync(null);
        return Results.Ok(secrets.Select(s => AccessPolicies.RedactFor(httpContext.User, s)));
    }

    private static async Task<IResult> CreateGlobal([FromBody] SecretRequest req, IFleetStorage storage)
    {
        var secret = new Secret { Name = req.Name, Value = req.Value };
        await storage.AddSecretAsync(secret);
        return Results.Created($"/api/secrets/{secret.Id}", secret);
    }

    private static async Task<IResult> UpdateGlobal(Guid id, [FromBody] SecretRequest req, IFleetStorage storage)
    {
        var secret = await storage.GetSecretAsync(id);
        if (secret is null) return Results.NotFound();
        if (secret.ProjectId is not null) return Results.BadRequest("Secret belongs to a project, not global.");

        secret.Name = req.Name;
        secret.Value = req.Value;
        secret.UpdatedAt = DateTimeOffset.UtcNow;
        await storage.UpdateSecretAsync(secret);
        return Results.Ok(secret);
    }

    private static async Task<IResult> DeleteGlobal(Guid id, IFleetStorage storage)
    {
        await storage.DeleteSecretAsync(id);
        return Results.NoContent();
    }

    // ── Per-project ───────────────────────────────────────────────────────────

    private static async Task<IResult> GetByProject(Guid projectId, IFleetStorage storage, HttpContext httpContext)
    {
        var secrets = await storage.GetSecretsAsync(projectId);
        return Results.Ok(secrets.Select(s => AccessPolicies.RedactFor(httpContext.User, s)));
    }

    private static async Task<IResult> CreateForProject(Guid projectId, [FromBody] SecretRequest req, IFleetStorage storage)
    {
        var project = await storage.GetProjectAsync(projectId);
        if (project is null) return Results.NotFound("Project not found.");

        var secret = new Secret { Name = req.Name, Value = req.Value, ProjectId = projectId };
        await storage.AddSecretAsync(secret);
        return Results.Created($"/api/projects/{projectId}/secrets/{secret.Id}", secret);
    }

    private static async Task<IResult> UpdateForProject(Guid projectId, Guid id, [FromBody] SecretRequest req, IFleetStorage storage)
    {
        var secret = await storage.GetSecretAsync(id);
        if (secret is null || secret.ProjectId != projectId) return Results.NotFound();

        secret.Name = req.Name;
        secret.Value = req.Value;
        secret.UpdatedAt = DateTimeOffset.UtcNow;
        await storage.UpdateSecretAsync(secret);
        return Results.Ok(secret);
    }

    private static async Task<IResult> DeleteForProject(Guid projectId, Guid id, IFleetStorage storage)
    {
        var secret = await storage.GetSecretAsync(id);
        if (secret is null || secret.ProjectId != projectId) return Results.NotFound();
        await storage.DeleteSecretAsync(id);
        return Results.NoContent();
    }

    public record SecretRequest(string Name, string Value);
}
