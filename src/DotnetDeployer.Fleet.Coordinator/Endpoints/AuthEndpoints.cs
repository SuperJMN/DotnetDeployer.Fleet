using static BCrypt.Net.BCrypt;
using System.Security.Claims;
using DotnetDeployer.Fleet.Coordinator.Auth;
using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DotnetDeployer.Fleet.Coordinator.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", Login);
        group.MapGet("/session", Session).RequireAuthorization();
        group.MapPost("/tokens", CreateApiToken).RequireAuthorization(AccessPolicies.Write);
        group.MapPost("/users", CreateUser).RequireAuthorization("Admin");
        group.MapGet("/users", ListUsers).RequireAuthorization("Admin");
        group.MapDelete("/users/{id:guid}", DeleteUser).RequireAuthorization("Admin");
    }

    private static async Task<IResult> Login(
        [FromBody] LoginRequest req,
        IFleetStorage storage,
        JwtService jwt)
    {
        var user = await storage.GetUserByUsernameAsync(req.Username.ToLower());
        if (user is null || !Verify(req.Password, user.PasswordHash))
            return Results.Unauthorized();

        var token = jwt.GenerateToken(user);
        return Results.Ok(new { token, username = user.Username, role = user.Role.ToString() });
    }

    private static IResult Session(HttpContext httpContext)
    {
        var username = httpContext.User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var role = httpContext.User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        return Results.Ok(new { username, role });
    }

    internal const int DefaultApiTokenDays = 90;
    internal const int MaxApiTokenDays = 365;

    /// <summary>
    /// Issues a long-lived token for an agent or script. Read-only by default, so an agent can
    /// diagnose without being able to deploy or read secrets.
    /// </summary>
    internal static IResult CreateApiToken(
        [FromBody] CreateApiTokenRequest? req,
        HttpContext httpContext,
        JwtService jwt)
    {
        var request = req ?? new CreateApiTokenRequest();
        var username = httpContext.User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        if (!Enum.TryParse<UserRole>(httpContext.User.FindFirstValue(ClaimTypes.Role), out var callerRole))
            return Results.Forbid();

        var role = request.ReadOnly ? UserRole.Reader : callerRole;
        var days = Math.Clamp(request.Days ?? DefaultApiTokenDays, 1, MaxApiTokenDays);
        var (token, expiresAt) = jwt.GenerateApiToken(userId, username, role, TimeSpan.FromDays(days));
        return Results.Ok(new ApiTokenResponse(token, username, role.ToString(), expiresAt));
    }

    private static async Task<IResult> CreateUser(
        [FromBody] CreateUserRequest req,
        IFleetStorage storage)
    {
        var existing = await storage.GetUserByUsernameAsync(req.Username.ToLower());
        if (existing is not null)
            return Results.Conflict(new { error = "Username already exists." });

        var user = new User
        {
            Username = req.Username.ToLower(),
            PasswordHash = HashPassword(req.Password),
            Role = Enum.TryParse<UserRole>(req.Role, true, out var role) ? role : UserRole.Operator
        };

        await storage.AddUserAsync(user);
        return Results.Created($"/api/auth/users/{user.Id}", new { user.Id, user.Username, user.Role });
    }

    private static async Task<IResult> ListUsers(IFleetStorage storage)
    {
        var users = await storage.GetUsersAsync();
        return Results.Ok(users.Select(u => new { u.Id, u.Username, u.Role, u.CreatedAt }));
    }

    private static async Task<IResult> DeleteUser(Guid id, IFleetStorage storage)
    {
        await storage.DeleteUserAsync(id);
        return Results.NoContent();
    }

    public record LoginRequest(string Username, string Password);
    public record CreateUserRequest(string Username, string Password, string Role = "Operator");
}
