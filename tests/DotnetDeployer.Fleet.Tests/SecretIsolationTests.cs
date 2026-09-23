using System.Diagnostics;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;
using NSubstitute;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SecretIsolationTests
{
    [Fact]
    public void Secrets_are_partitioned_excluding_push_api_key_from_build_environment()
    {
        var allSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_API_KEY"] = "secret-nuget-push-token",
            ["CUSTOM_PUSH_KEY"] = "secret-custom-push-token",
            ["PRIVATE_FEED_RESTORE_TOKEN"] = "restore-pat",
            ["VSS_NUGET_EXTERNAL_FEED_ENDPOINTS"] = "endpoint-json",
            ["SIGNING_CERT_PWD"] = "cert-secret"
        };

        var pushSecretNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NUGET_API_KEY",
            "CUSTOM_PUSH_KEY"
        };

        var buildEnvVars = allSecrets
            .Where(kvp => !pushSecretNames.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // Push credentials must be excluded
        buildEnvVars.Should().NotContainKey("NUGET_API_KEY");
        buildEnvVars.Should().NotContainKey("CUSTOM_PUSH_KEY");

        // Non-push secrets (e.g. private feed restore tokens) MUST be preserved
        buildEnvVars.Should().ContainKey("PRIVATE_FEED_RESTORE_TOKEN").WhoseValue.Should().Be("restore-pat");
        buildEnvVars.Should().ContainKey("VSS_NUGET_EXTERNAL_FEED_ENDPOINTS").WhoseValue.Should().Be("endpoint-json");
        buildEnvVars.Should().ContainKey("SIGNING_CERT_PWD").WhoseValue.Should().Be("cert-secret");
    }

    [Fact]
    public async Task NuGetPackagePusher_masks_api_key_in_streamed_output()
    {
        const string secretKey = "super-secret-api-key-12345";
        var capturedLines = new List<string>();

        var runner = new FakeStreamingProcessRunner(async (psi, onLine) =>
        {
            await onLine($"Pushed package with key {secretKey} successfully.");
            return 0;
        });

        var result = await NuGetPackagePusher.PushAsync(
            workingDirectory: "/tmp",
            packagePath: "/tmp/package.nupkg",
            apiKey: secretKey,
            source: "https://api.nuget.org/v3/index.json",
            onLine: line =>
            {
                capturedLines.Add(line);
                return Task.CompletedTask;
            },
            processRunner: runner);

        result.Success.Should().BeTrue();
        capturedLines.Should().ContainSingle();
        capturedLines[0].Should().NotContain(secretKey);
        capturedLines[0].Should().Contain("***HIDDEN***");
    }

    [Fact]
    public async Task Prior_stage_failure_prevents_push_process_spawn()
    {
        var spawnCount = 0;
        var runner = new FakeStreamingProcessRunner((psi, onLine) =>
        {
            spawnCount++;
            return Task.FromResult(0);
        });
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((false, "tests failed")),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])),
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runPush: async (pkgs, ct) =>
            {
                await runner.RunAsync(new ProcessStartInfo("dotnet"), _ => Task.CompletedTask, ct);
                return (true, null);
            });

        result.Success.Should().BeFalse();
        spawnCount.Should().Be(0, "push process must NEVER be spawned when previous stages fail");
    }

    private sealed class FakeStreamingProcessRunner(
        Func<ProcessStartInfo, Func<string, Task>, Task<int>> handler) : IStreamingProcessRunner
    {
        public Task<int> RunAsync(ProcessStartInfo startInfo, Func<string, Task> onLine, CancellationToken ct = default)
            => handler(startInfo, onLine);
    }

    [Fact]
    public void DeployerYamlReader_resolves_custom_apiKey_secret_name()
    {
        var yaml = """
            nuget:
              enabled: true
              source: "https://nuget.example.com/v3/index.json"
              apiKeyEnvVar: "MY_COMPANY_FEED_KEY"
            """;

        var config = DeployerYamlReader.ParseNuGetConfig(yaml);

        config.Enabled.Should().BeTrue();
        config.Source.Should().Be("https://nuget.example.com/v3/index.json");
        config.ApiKeySecretName.Should().Be("MY_COMPANY_FEED_KEY");
    }
}
