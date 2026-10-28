namespace DotnetDeployer.Fleet.Core.Api;

/// <summary>Latest commit of a project's tracked branch, as the coordinator resolves it before a deploy.</summary>
public sealed record ProjectHead(string Branch, string CommitSha);
