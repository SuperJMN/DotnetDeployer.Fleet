using YamlDotNet.RepresentationModel;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

public sealed record NuGetDeployConfig(
    bool Enabled,
    string Source,
    string ApiKeySecretName);

public static class DeployerYamlReader
{
    public static NuGetDeployConfig ReadNuGetConfig(string repoRoot)
    {
        var yamlPath = FindDeployerYaml(repoRoot);
        if (yamlPath is null || !File.Exists(yamlPath))
            return new NuGetDeployConfig(false, "https://api.nuget.org/v3/index.json", "NUGET_API_KEY");

        try
        {
            var content = File.ReadAllText(yamlPath);
            return ParseNuGetConfig(content);
        }
        catch
        {
            return new NuGetDeployConfig(false, "https://api.nuget.org/v3/index.json", "NUGET_API_KEY");
        }
    }

    public static NuGetDeployConfig ParseNuGetConfig(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
            return new NuGetDeployConfig(false, "https://api.nuget.org/v3/index.json", "NUGET_API_KEY");

        using var reader = new StringReader(yamlContent);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0 ||
            stream.Documents[0].RootNode is not YamlMappingNode root ||
            !TryGetMappingValue(root, "nuget", out var nugetNode) ||
            nugetNode is not YamlMappingNode nuget)
        {
            return new NuGetDeployConfig(false, "https://api.nuget.org/v3/index.json", "NUGET_API_KEY");
        }

        var enabled = true;
        if (TryGetMappingValue(nuget, "enabled", out var enabledNode) &&
            enabledNode is YamlScalarNode enabledScalar &&
            bool.TryParse(enabledScalar.Value, out var parsedEnabled))
        {
            enabled = parsedEnabled;
        }

        var source = "https://api.nuget.org/v3/index.json";
        if (TryGetMappingValue(nuget, "source", out var sourceNode) &&
            sourceNode is YamlScalarNode sourceScalar &&
            !string.IsNullOrWhiteSpace(sourceScalar.Value))
        {
            source = sourceScalar.Value.Trim();
        }

        var apiKeySecretName = "NUGET_API_KEY";
        if (TryGetMappingValue(nuget, "apiKeyEnvVar", out var keyEnvNode) &&
            keyEnvNode is YamlScalarNode keyEnvScalar &&
            !string.IsNullOrWhiteSpace(keyEnvScalar.Value))
        {
            apiKeySecretName = keyEnvScalar.Value.Trim();
        }
        else if (TryGetMappingValue(nuget, "apiKey", out var apiKeyNode))
        {
            if (apiKeyNode is YamlScalarNode scalarKey && !string.IsNullOrWhiteSpace(scalarKey.Value))
            {
                apiKeySecretName = scalarKey.Value.Trim();
            }
            else if (apiKeyNode is YamlMappingNode keyMapping)
            {
                if (TryGetMappingValue(keyMapping, "name", out var nameNode) &&
                    nameNode is YamlScalarNode nameScalar &&
                    !string.IsNullOrWhiteSpace(nameScalar.Value))
                {
                    apiKeySecretName = nameScalar.Value.Trim();
                }
                else if (TryGetMappingValue(keyMapping, "key", out var keySubNode) &&
                         keySubNode is YamlScalarNode keySubScalar &&
                         !string.IsNullOrWhiteSpace(keySubScalar.Value))
                {
                    apiKeySecretName = keySubScalar.Value.Trim();
                }
            }
        }

        return new NuGetDeployConfig(enabled, source, apiKeySecretName);
    }

    private static string? FindDeployerYaml(string root)
    {
        var yaml = Path.Combine(root, "deployer.yaml");
        if (File.Exists(yaml)) return yaml;

        var yml = Path.Combine(root, "deployer.yml");
        return File.Exists(yml) ? yml : null;
    }

    private static bool TryGetMappingValue(YamlMappingNode mapping, string key, out YamlNode value)
    {
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                value = child.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }
}
