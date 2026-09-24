using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDeployer.Fleet.Core.Domain;

public class PackageBuildRequest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public string? PackageProject { get; set; }
    public string? CommitSha { get; set; }
    public List<PackageBuildTarget> Targets { get; set; } = [];

    public static string Serialize(PackageBuildRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static PackageBuildRequest Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new PackageBuildRequest();

        var request = JsonSerializer.Deserialize<PackageBuildRequest>(json, JsonOptions)
                      ?? new PackageBuildRequest();
        request.Targets ??= [];
        return request;
    }
}

public class PackageBuildTarget
{
    public string Format { get; set; } = "";
    public string Architecture { get; set; } = "";

    public string ToDeployerTarget() => $"{Format}:{Architecture}";
}
