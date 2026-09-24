using YamlDotNet.RepresentationModel;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

public sealed record NuGetDeployConfig(
    bool Enabled,
    string Source,
    string ApiKeySecretName);

public sealed record GitHubDeployConfig(
    bool Enabled,
    string? Owner,
    string? Repo,
    string? TokenSecretName);

public sealed record GitHubPagesDeployConfig(
    bool Enabled);

public sealed record DeployerConfigSummary(
    NuGetDeployConfig NuGet,
    GitHubDeployConfig GitHub,
    GitHubPagesDeployConfig GitHubPages,
    IReadOnlySet<string> PublishSecretNames,
    IReadOnlySet<string> SigningSecretNames);

public static class DeployerYamlReader
{
    private static readonly HashSet<string> WellKnownPublishSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "NUGET_API_KEY",
        "GITHUB_TOKEN",
        "GH_TOKEN"
    };

    private static readonly HashSet<string> WellKnownSigningSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANDROID_KEYSTORE_BASE64",
        "ANDROID_SIGNING_STORE_PASS",
        "ANDROID_SIGNING_KEY_PASS"
    };

    public static NuGetDeployConfig ReadNuGetConfig(string repoRoot) =>
        ReadConfig(repoRoot).NuGet;

    public static NuGetDeployConfig ParseNuGetConfig(string yamlContent) =>
        ParseConfig(yamlContent).NuGet;

    public static DeployerConfigSummary ReadConfig(string repoRoot)
    {
        var yamlPath = FindDeployerYaml(repoRoot);
        if (yamlPath is null || !File.Exists(yamlPath))
            return CreateDefaultConfig();

        try
        {
            var content = File.ReadAllText(yamlPath);
            return ParseConfig(content);
        }
        catch
        {
            return CreateDefaultConfig();
        }
    }

    public static DeployerConfigSummary ParseConfig(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
            return CreateDefaultConfig();

        var publishSecrets = new HashSet<string>(WellKnownPublishSecrets, StringComparer.OrdinalIgnoreCase);
        var signingSecrets = new HashSet<string>(WellKnownSigningSecrets, StringComparer.OrdinalIgnoreCase);

        try
        {
            using var reader = new StringReader(yamlContent);
            var stream = new YamlStream();
            stream.Load(reader);

            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return CreateDefaultConfig();
            }

            var nugetConfig = ParseNuGetSection(root, publishSecrets);
            var githubConfig = ParseGitHubSection(root, publishSecrets);
            var pagesConfig = ParsePagesSection(root);

            CollectSigningSecrets(root, signingSecrets);

            return new DeployerConfigSummary(nugetConfig, githubConfig, pagesConfig, publishSecrets, signingSecrets);
        }
        catch
        {
            return CreateDefaultConfig();
        }
    }

    private static DeployerConfigSummary CreateDefaultConfig()
    {
        return new DeployerConfigSummary(
            new NuGetDeployConfig(false, "https://api.nuget.org/v3/index.json", "NUGET_API_KEY"),
            new GitHubDeployConfig(false, null, null, "GITHUB_TOKEN"),
            new GitHubPagesDeployConfig(false),
            new HashSet<string>(WellKnownPublishSecrets, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(WellKnownSigningSecrets, StringComparer.OrdinalIgnoreCase));
    }

    private static NuGetDeployConfig ParseNuGetSection(YamlMappingNode root, HashSet<string> publishSecrets)
    {
        if (!TryGetMappingValue(root, "nuget", out var nugetNode) ||
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

        if (!string.IsNullOrWhiteSpace(apiKeySecretName))
            publishSecrets.Add(apiKeySecretName);

        return new NuGetDeployConfig(enabled, source, apiKeySecretName);
    }

    private static GitHubDeployConfig ParseGitHubSection(YamlMappingNode root, HashSet<string> publishSecrets)
    {
        if (!TryGetMappingValue(root, "github", out var githubNode) ||
            githubNode is not YamlMappingNode github)
        {
            return new GitHubDeployConfig(false, null, null, "GITHUB_TOKEN");
        }

        var enabled = true;
        if (TryGetMappingValue(github, "enabled", out var enabledNode) &&
            enabledNode is YamlScalarNode enabledScalar &&
            bool.TryParse(enabledScalar.Value, out var parsedEnabled))
        {
            enabled = parsedEnabled;
        }

        string? owner = null;
        if (TryGetMappingValue(github, "owner", out var ownerNode) &&
            ownerNode is YamlScalarNode ownerScalar &&
            !string.IsNullOrWhiteSpace(ownerScalar.Value))
        {
            owner = ownerScalar.Value.Trim();
        }

        string? repo = null;
        if (TryGetMappingValue(github, "repo", out var repoNode) &&
            repoNode is YamlScalarNode repoScalar &&
            !string.IsNullOrWhiteSpace(repoScalar.Value))
        {
            repo = repoScalar.Value.Trim();
        }

        var tokenSecretName = "GITHUB_TOKEN";
        if (TryGetMappingValue(github, "token", out var tokenNode))
        {
            if (tokenNode is YamlScalarNode scalarToken && !string.IsNullOrWhiteSpace(scalarToken.Value))
            {
                tokenSecretName = scalarToken.Value.Trim();
            }
            else if (tokenNode is YamlMappingNode tokenMapping)
            {
                if (TryGetMappingValue(tokenMapping, "name", out var nameNode) &&
                    nameNode is YamlScalarNode nameScalar &&
                    !string.IsNullOrWhiteSpace(nameScalar.Value))
                {
                    tokenSecretName = nameScalar.Value.Trim();
                }
                else if (TryGetMappingValue(tokenMapping, "env", out var envNode) &&
                         envNode is YamlScalarNode envScalar &&
                         !string.IsNullOrWhiteSpace(envScalar.Value))
                {
                    tokenSecretName = envScalar.Value.Trim();
                }
                else if (TryGetMappingValue(tokenMapping, "key", out var keySubNode) &&
                         keySubNode is YamlScalarNode keySubScalar &&
                         !string.IsNullOrWhiteSpace(keySubScalar.Value))
                {
                    tokenSecretName = keySubScalar.Value.Trim();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(tokenSecretName))
            publishSecrets.Add(tokenSecretName);

        return new GitHubDeployConfig(enabled, owner, repo, tokenSecretName);
    }

    private static GitHubPagesDeployConfig ParsePagesSection(YamlMappingNode root)
    {
        if (!TryGetMappingValue(root, "githubPages", out var pagesNode) ||
            pagesNode is not YamlMappingNode pages)
        {
            return new GitHubPagesDeployConfig(false);
        }

        var enabled = true;
        if (TryGetMappingValue(pages, "enabled", out var enabledNode) &&
            enabledNode is YamlScalarNode enabledScalar &&
            bool.TryParse(enabledScalar.Value, out var parsedEnabled))
        {
            enabled = parsedEnabled;
        }

        return new GitHubPagesDeployConfig(enabled);
    }

    private static void CollectSigningSecrets(YamlNode node, HashSet<string> signingSecrets)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var child in mapping.Children)
            {
                if (child.Key is YamlScalarNode scalar &&
                    string.Equals(scalar.Value, "signing", StringComparison.OrdinalIgnoreCase) &&
                    child.Value is YamlMappingNode signingMapping)
                {
                    ExtractSecretNamesFromSigning(signingMapping, signingSecrets);
                }
                else
                {
                    CollectSigningSecrets(child.Value, signingSecrets);
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var item in sequence.Children)
            {
                CollectSigningSecrets(item, signingSecrets);
            }
        }
    }

    private static void ExtractSecretNamesFromSigning(YamlMappingNode signing, HashSet<string> signingSecrets)
    {
        foreach (var entry in signing.Children)
        {
            if (entry.Value is YamlMappingNode valueMapping)
            {
                if (TryGetMappingValue(valueMapping, "name", out var nameNode) &&
                    nameNode is YamlScalarNode nameScalar &&
                    !string.IsNullOrWhiteSpace(nameScalar.Value))
                {
                    signingSecrets.Add(nameScalar.Value.Trim());
                }
                else if (TryGetMappingValue(valueMapping, "key", out var keyNode) &&
                         keyNode is YamlScalarNode keyScalar &&
                         !string.IsNullOrWhiteSpace(keyScalar.Value))
                {
                    signingSecrets.Add(keyScalar.Value.Trim());
                }
            }
        }
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
