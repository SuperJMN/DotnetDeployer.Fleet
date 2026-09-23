using System.Diagnostics;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SecretIsolationTests
{
    [Fact]
    public void Secrets_are_partitioned_excluding_all_publish_secrets_from_build_environment()
    {
        var allSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_API_KEY"] = "secret-nuget-push-token",
            ["CUSTOM_PUSH_KEY"] = "secret-custom-push-token",
            ["GITHUB_TOKEN"] = "secret-github-token",
            ["GH_TOKEN"] = "secret-gh-token",
            ["CUSTOM_GH_TOKEN"] = "secret-custom-gh-token",
            ["ANDROID_KEYSTORE_BASE64"] = "keystore-base64",
            ["PRIVATE_FEED_RESTORE_TOKEN"] = "restore-pat",
            ["VSS_NUGET_EXTERNAL_FEED_ENDPOINTS"] = "endpoint-json",
            ["SIGNING_CERT_PWD"] = "cert-secret"
        };

        var publishSecretNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NUGET_API_KEY",
            "CUSTOM_PUSH_KEY",
            "GITHUB_TOKEN",
            "GH_TOKEN",
            "CUSTOM_GH_TOKEN",
            "ANDROID_KEYSTORE_BASE64"
        };

        var buildEnvVars = allSecrets
            .Where(kvp => !publishSecretNames.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // Publish credentials must be strictly excluded from build/test environment
        buildEnvVars.Should().NotContainKey("NUGET_API_KEY");
        buildEnvVars.Should().NotContainKey("CUSTOM_PUSH_KEY");
        buildEnvVars.Should().NotContainKey("GITHUB_TOKEN");
        buildEnvVars.Should().NotContainKey("GH_TOKEN");
        buildEnvVars.Should().NotContainKey("CUSTOM_GH_TOKEN");
        buildEnvVars.Should().NotContainKey("ANDROID_KEYSTORE_BASE64");

        // Restore and other non-publish secrets MUST be preserved
        buildEnvVars.Should().ContainKey("PRIVATE_FEED_RESTORE_TOKEN").WhoseValue.Should().Be("restore-pat");
        buildEnvVars.Should().ContainKey("VSS_NUGET_EXTERNAL_FEED_ENDPOINTS").WhoseValue.Should().Be("endpoint-json");
        buildEnvVars.Should().ContainKey("SIGNING_CERT_PWD").WhoseValue.Should().Be("cert-secret");
    }

    [Fact]
    public void DeployerYamlReader_extracts_all_publish_secret_names()
    {
        var yaml = """
            github:
              enabled: true
              token:
                from: env
                name: "MY_GH_SECRET"
              packages:
                - project: app.csproj
                  signing:
                    keystore:
                      from: env
                      name: "MY_KEYSTORE_SECRET"
            nuget:
              enabled: true
              apiKeyEnvVar: "MY_NUGET_SECRET"
            """;

        var config = DeployerYamlReader.ParseConfig(yaml);

        config.PublishSecretNames.Should().Contain("NUGET_API_KEY");
        config.PublishSecretNames.Should().Contain("GITHUB_TOKEN");
        config.PublishSecretNames.Should().Contain("GH_TOKEN");
        config.PublishSecretNames.Should().Contain("MY_NUGET_SECRET");
        config.PublishSecretNames.Should().Contain("MY_GH_SECRET");
        config.PublishSecretNames.Should().Contain("MY_KEYSTORE_SECRET");
    }

    [Fact]
    public void CreateDotnetProcessStartInfo_removes_ambient_publish_secrets_from_environment()
    {
        const string sentinelNuget = "sentinel-nuget-ambient-secret-999";
        const string sentinelGithub = "sentinel-github-ambient-secret-888";

        Environment.SetEnvironmentVariable("NUGET_API_KEY", sentinelNuget);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", sentinelGithub);

        try
        {
            var scrubKeys = new[] { "NUGET_API_KEY", "GITHUB_TOKEN" };
            var envVars = new Dictionary<string, string>
            {
                ["PRIVATE_FEED_RESTORE_TOKEN"] = "restore-token-abc"
            };

            var psi = DeployerRunner.CreateDotnetProcessStartInfo(
                Directory.GetCurrentDirectory(),
                ["--info"],
                envVars,
                scrubKeys);

            psi.Environment.Should().NotContainKey("NUGET_API_KEY");
            psi.Environment.Should().NotContainKey("GITHUB_TOKEN");
            psi.Environment.Should().ContainKey("PRIVATE_FEED_RESTORE_TOKEN")
                .WhoseValue.Should().Be("restore-token-abc");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_API_KEY", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        }
    }

    [Fact]
    public async Task Real_process_execution_scrubs_ambient_publish_secrets_and_preserves_restore_credentials()
    {
        const string sentinelAmbientNuget = "SENTINEL_AMBIENT_NUGET_SECRET_VAL";
        const string sentinelAmbientGithub = "SENTINEL_AMBIENT_GITHUB_SECRET_VAL";
        const string sentinelRestore = "SENTINEL_RESTORE_SECRET_VAL";

        Environment.SetEnvironmentVariable("NUGET_API_KEY", sentinelAmbientNuget);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", sentinelAmbientGithub);

        try
        {
            var scrubKeys = new[] { "NUGET_API_KEY", "GITHUB_TOKEN" };
            var envVars = new Dictionary<string, string>
            {
                ["PRIVATE_FEED_RESTORE_TOKEN"] = sentinelRestore
            };

            // Launch a real child process using ProcessStartInfo configured by DeployerRunner
            var isWindows = OperatingSystem.IsWindows();
            var shellExe = isWindows ? "cmd.exe" : "sh";
            var shellArgs = isWindows
                ? new[] { "/c", "echo NUGET=%NUGET_API_KEY%|GH=%GITHUB_TOKEN%|RESTORE=%PRIVATE_FEED_RESTORE_TOKEN%" }
                : new[] { "-c", "echo \"NUGET=$NUGET_API_KEY|GH=$GITHUB_TOKEN|RESTORE=$PRIVATE_FEED_RESTORE_TOKEN\"" };

            var psi = new ProcessStartInfo(shellExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var arg in shellArgs)
                psi.ArgumentList.Add(arg);

            // Apply DeployerRunner's build environment and secret scrubbing
            DeployerRunner.ApplyBuildEnvironment(psi);

            foreach (var key in scrubKeys)
                psi.Environment.Remove(key);

            foreach (var (k, v) in envVars)
                psi.Environment[k] = v;

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0);

            // Verify: sentinel ambient publish secrets are NOT present in the child process output
            output.Should().NotContain(sentinelAmbientNuget);
            output.Should().NotContain(sentinelAmbientGithub);

            // Verify: sentinel restore credential IS present in child process output
            output.Should().Contain(sentinelRestore);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_API_KEY", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        }
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
}
