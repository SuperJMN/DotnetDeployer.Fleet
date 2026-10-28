using System.Text.Json;

namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>
/// Lists the versions a NuGet v3 feed holds for a package (its flat-container index), to spot
/// versions Fleet did not publish — for example, an old pipeline that still pushes releases.
/// </summary>
public sealed class NuGetFeedVersions(HttpClient? client = null)
{
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    private readonly HttpClient http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlyList<string>> ListAsync(string source, string packageId, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var indexUri) || indexUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"Only HTTP v3 feeds can be compared; '{source}' is not one.");

        using var indexResponse = await http.GetAsync(indexUri, ct);
        indexResponse.EnsureSuccessStatusCode();
        using var index = JsonDocument.Parse(await indexResponse.Content.ReadAsStringAsync(ct));
        var baseAddress = index.RootElement.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.TryGetProperty("@type", out var type)
                               && type.ToString().StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal))
            .Select(resource => resource.GetProperty("@id").GetString())
            .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address))
            ?? throw new InvalidOperationException($"The feed '{source}' has no PackageBaseAddress resource.");

        var url = new Uri(new Uri(baseAddress.TrimEnd('/') + "/"), $"{packageId.ToLowerInvariant()}/index.json");
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();

        using var versions = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return versions.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString())
            .OfType<string>()
            .ToList();
    }
}
