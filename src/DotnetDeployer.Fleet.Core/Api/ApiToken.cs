namespace DotnetDeployer.Fleet.Core.Api;

/// <summary>Request for a long-lived API token. Tokens are read-only unless <see cref="ReadOnly"/> is false.</summary>
public sealed record CreateApiTokenRequest(bool ReadOnly = true, int? Days = null);

public sealed record ApiTokenResponse(string Token, string Username, string Role, DateTimeOffset ExpiresAt);
