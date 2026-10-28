using System.Xml.Linq;
using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using YamlDotNet.RepresentationModel;

namespace DotnetDeployer.Fleet.Coordinator.Services;

/// <summary>
/// Checks a project against Fleet's requirements without queueing a job: it clones the tracked
/// branch and inspects the root solution, deployer.yaml, the secrets it references, the packages
/// a release would publish and the workers able to build it.
/// </summary>
public sealed class ProjectPreflight(IFleetStorage storage)
{
    private static readonly HashSet<string> AndroidFormats = new(StringComparer.OrdinalIgnoreCase) { "apk", "aab" };

    public async Task<ProjectPreflightReport> RunAsync(Project project, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dotnetfleet-preflight", $"{project.Id:N}-{Guid.NewGuid():N}");
        try
        {
            await ProjectRepositoryCheckout.CloneShallow(project, tempDir, ct);

            var secretNames = (await storage.GetSecretsAsync(null, ct))
                .Concat(await storage.GetSecretsAsync(project.Id, ct))
                .Select(s => s.Name)
                .ToList();
            var workers = await storage.GetWorkersAsync(ct);

            return Inspect(tempDir, project, secretNames, workers);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the report is already computed.
            }
        }
    }

    internal static ProjectPreflightReport Inspect(
        string repositoryRoot,
        Project project,
        IReadOnlyCollection<string> availableSecretNames,
        IReadOnlyList<Worker> workers)
    {
        var checks = new List<PreflightCheck> { CheckSolution(repositoryRoot) };

        var yamlPath = new[] { "deployer.yaml", "deployer.yml" }
            .Select(name => Path.Combine(repositoryRoot, name))
            .FirstOrDefault(File.Exists);

        YamlMappingNode? root = null;
        if (yamlPath is null)
        {
            checks.Add(new PreflightCheck("deployer.yaml", PreflightSeverity.Error,
                "No deployer.yaml (or deployer.yml) in the repository root."));
        }
        else
        {
            try
            {
                root = LoadYaml(File.ReadAllText(yamlPath));
                checks.Add(new PreflightCheck("deployer.yaml", PreflightSeverity.Ok, $"Found {Path.GetFileName(yamlPath)}."));
            }
            catch (Exception ex)
            {
                checks.Add(new PreflightCheck("deployer.yaml", PreflightSeverity.Error, $"deployer.yaml cannot be parsed: {ex.Message}"));
            }
        }

        var referencedSecrets = root is null ? [] : ReferencedSecrets(root);
        var available = new HashSet<string>(availableSecretNames, StringComparer.OrdinalIgnoreCase);
        var missingSecrets = referencedSecrets.Where(name => !available.Contains(name)).ToList();
        checks.Add(missingSecrets.Count == 0
            ? new PreflightCheck("secrets", PreflightSeverity.Ok,
                referencedSecrets.Count == 0
                    ? "deployer.yaml references no secrets."
                    : $"All {referencedSecrets.Count} referenced secret(s) exist in Fleet.")
            : new PreflightCheck("secrets", PreflightSeverity.Error,
                $"Missing secret(s) referenced by deployer.yaml: {string.Join(", ", missingSecrets)}."));

        var packageIds = root is null ? [] : NuGetPackageIds(repositoryRoot, root);
        checks.Add(CheckPinnedPackages(project, packageIds));

        var requirements = root is null ? [] : Requirements(root);
        var suitability = workers.Select(worker => Suitability(worker, requirements)).ToList();
        checks.Add(CheckWorkers(suitability));

        return new ProjectPreflightReport(
            project.Id,
            project.Name,
            checks,
            referencedSecrets,
            missingSecrets,
            packageIds,
            requirements,
            suitability);
    }

    private static PreflightCheck CheckSolution(string repositoryRoot)
    {
        var candidates = Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var slnx = candidates.Where(name => name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).ToList();
        var sln = candidates.Except(slnx).ToList();

        // Same preference as the worker's test gate and DotnetDeployer: one .slnx wins, otherwise one .sln.
        if (slnx.Count == 1 || (slnx.Count == 0 && sln.Count == 1))
            return new PreflightCheck("solution", PreflightSeverity.Ok, $"Root solution: {(slnx.Count == 1 ? slnx[0] : sln[0])}.");

        return new PreflightCheck("solution", PreflightSeverity.Error,
            candidates.Count == 0
                ? "No .slnx or .sln file in the repository root."
                : $"Expected a single .slnx or .sln in the repository root, found: {string.Join(", ", candidates)}.");
    }

    private static PreflightCheck CheckPinnedPackages(Project project, IReadOnlyList<string> packageIds)
    {
        if (project.ExpectedPackageIds.Count == 0)
        {
            return new PreflightCheck("packages", PreflightSeverity.Ok,
                packageIds.Count == 0
                    ? "No NuGet packages to publish and none pinned."
                    : $"Not pinned. A release would publish: {string.Join(", ", packageIds)}.");
        }

        var missing = project.ExpectedPackageIds.Except(packageIds, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = packageIds.Except(project.ExpectedPackageIds, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0 && extra.Count == 0)
            return new PreflightCheck("packages", PreflightSeverity.Ok, "Packable projects match the pinned packages.");

        // The ids are inferred from project files, so a mismatch is a strong hint rather than a certainty.
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"pinned but not found: {string.Join(", ", missing)}");
        if (extra.Count > 0) parts.Add($"found but not pinned: {string.Join(", ", extra)}");
        return new PreflightCheck("packages", PreflightSeverity.Warning,
            $"Packable projects do not match the pinned packages ({string.Join("; ", parts)}); the release would fail.");
    }

    private static PreflightCheck CheckWorkers(IReadOnlyList<WorkerSuitability> workers)
    {
        if (workers.Count == 0)
            return new PreflightCheck("workers", PreflightSeverity.Error, "No workers are registered.");

        var capable = workers.Where(w => w.CanBuild).ToList();
        if (capable.Count == 0)
            return new PreflightCheck("workers", PreflightSeverity.Error, "No registered worker can build this project.");

        if (capable.All(w => w.Status == WorkerStatus.Offline))
            return new PreflightCheck("workers", PreflightSeverity.Warning,
                $"Only offline workers can build this project: {string.Join(", ", capable.Select(w => w.Name))}.");

        return new PreflightCheck("workers", PreflightSeverity.Ok,
            $"Workers that can build it: {string.Join(", ", capable.Select(w => w.Name))}.");
    }

    internal static WorkerSuitability Suitability(Worker worker, IReadOnlyList<string> requirements)
    {
        var reasons = new List<string>();
        if (requirements.Contains("android"))
        {
            if (worker.Environment is null)
                reasons.Add("has not reported its build environment (update the worker)");
            else
            {
                if (string.IsNullOrWhiteSpace(worker.Environment.AndroidSdkPath))
                    reasons.Add("no Android SDK (ANDROID_HOME / ANDROID_SDK_ROOT)");
                if (string.IsNullOrWhiteSpace(worker.Environment.JavaHome))
                    reasons.Add("no JDK (JAVA_HOME)");
            }
        }

        if (worker.Status == WorkerStatus.Offline)
            reasons.Add("offline");

        var canBuild = !reasons.Any(r => r != "offline");
        return new WorkerSuitability(worker.Id, worker.Name, worker.Status, canBuild, reasons);
    }

    private static YamlMappingNode? LoadYaml(string content)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(content));
        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
    }

    /// <summary>
    /// Environment variables deployer.yaml reads secrets from: every <c>{ from: env, name: X }</c>
    /// mapping, plus the legacy <c>apiKeyEnvVar</c> scalar.
    /// </summary>
    internal static IReadOnlyList<string> ReferencedSecrets(YamlNode root)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(root);
        return names.ToList();

        void Collect(YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode mapping:
                    if (Scalar(mapping, "from") is { } from
                        && string.Equals(from, "env", StringComparison.OrdinalIgnoreCase)
                        && Scalar(mapping, "name") is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }

                    if (Scalar(mapping, "apiKeyEnvVar") is { Length: > 0 } envVar)
                        names.Add(envVar);

                    foreach (var child in mapping.Children.Values)
                        Collect(child);
                    break;
                case YamlSequenceNode sequence:
                    foreach (var child in sequence.Children)
                        Collect(child);
                    break;
            }
        }
    }

    /// <summary>Capabilities a worker needs to build the project, e.g. <c>android</c>.</summary>
    internal static IReadOnlyList<string> Requirements(YamlNode root)
    {
        var requirements = new SortedSet<string>(StringComparer.Ordinal);
        Collect(root);
        return requirements.ToList();

        void Collect(YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode mapping:
                    if (Scalar(mapping, "type") is { } type && AndroidFormats.Contains(type))
                        requirements.Add("android");
                    foreach (var child in mapping.Children.Values)
                        Collect(child);
                    break;
                case YamlSequenceNode sequence:
                    foreach (var child in sequence.Children)
                        Collect(child);
                    break;
            }
        }
    }

    /// <summary>
    /// Package ids a NuGet release would produce: the projects listed under <c>nuget.packages</c>,
    /// or every project marked packable when the list is absent.
    /// </summary>
    internal static IReadOnlyList<string> NuGetPackageIds(string repositoryRoot, YamlMappingNode root)
    {
        if (Child(root, "nuget") is not YamlMappingNode nuget)
            return [];
        if (Scalar(nuget, "enabled") is { } enabled && bool.TryParse(enabled, out var isEnabled) && !isEnabled)
            return [];

        IEnumerable<string> projectFiles;
        if (Child(nuget, "packages") is YamlSequenceNode packages)
        {
            projectFiles = packages.Children
                .OfType<YamlMappingNode>()
                .Select(package => Scalar(package, "project"))
                .OfType<string>()
                .Select(path => Path.Combine(repositoryRoot, path.Replace('\\', '/')));
        }
        else
        {
            projectFiles = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                .Where(IsPackable);
        }

        return projectFiles
            .Select(PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPackable(string projectFile)
    {
        var properties = ReadProperties(projectFile);
        return IsTrue(properties, "IsPackable") || IsTrue(properties, "PackAsTool");
    }

    private static string PackageId(string projectFile)
    {
        var properties = ReadProperties(projectFile);
        return properties.GetValueOrDefault("PackageId")
               ?? properties.GetValueOrDefault("AssemblyName")
               ?? Path.GetFileNameWithoutExtension(projectFile);
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) && bool.TryParse(value, out var flag) && flag;

    private static IReadOnlyDictionary<string, string> ReadProperties(string projectFile)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(projectFile))
            return properties;

        try
        {
            var document = XDocument.Load(projectFile);
            foreach (var element in document.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                var value = element.Value.Trim();
                // MSBuild expressions cannot be evaluated here; ignore them rather than report them verbatim.
                if (value.Length > 0 && !value.Contains("$("))
                    properties[element.Name.LocalName] = value;
            }
        }
        catch
        {
            // An unreadable project file falls back to its file name.
        }

        return properties;
    }

    private static YamlNode? Child(YamlMappingNode mapping, string key) =>
        mapping.Children
            .Where(pair => pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        Child(mapping, key) is YamlScalarNode { Value: { } value } ? value.Trim() : null;
}
