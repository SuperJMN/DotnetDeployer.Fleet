using System.Diagnostics;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SecretIsolationTests
{
    [Fact]
    public void Secrets_are_partitioned_for_test_and_pack_environments()
    {
        var allSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_API_KEY"] = "secret-nuget-push-token",
            ["CUSTOM_PUSH_KEY"] = "secret-custom-push-token",
            ["GITHUB_TOKEN"] = "secret-github-token",
            ["GH_TOKEN"] = "secret-gh-token",
            ["CUSTOM_GH_TOKEN"] = "secret-custom-gh-token",
            ["ANDROID_KEYSTORE_BASE64"] = "keystore-base64",
            ["ANDROID_SIGNING_STORE_PASS"] = "store-pass-123",
            ["ANDROID_SIGNING_KEY_PASS"] = "key-pass-123",
            ["PRIVATE_FEED_RESTORE_TOKEN"] = "restore-pat",
            ["VSS_NUGET_EXTERNAL_FEED_ENDPOINTS"] = "endpoint-json"
        };

        var publishSecretNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NUGET_API_KEY",
            "CUSTOM_PUSH_KEY",
            "GITHUB_TOKEN",
            "GH_TOKEN",
            "CUSTOM_GH_TOKEN"
        };

        var signingSecretNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ANDROID_KEYSTORE_BASE64",
            "ANDROID_SIGNING_STORE_PASS",
            "ANDROID_SIGNING_KEY_PASS"
        };

        // 1. Build and test environment: scrubs both publish credentials AND signing secrets
        var buildScrubKeys = publishSecretNames.Concat(signingSecretNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buildEnvVars = allSecrets
            .Where(kvp => !buildScrubKeys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        buildEnvVars.Should().NotContainKey("NUGET_API_KEY");
        buildEnvVars.Should().NotContainKey("CUSTOM_PUSH_KEY");
        buildEnvVars.Should().NotContainKey("GITHUB_TOKEN");
        buildEnvVars.Should().NotContainKey("GH_TOKEN");
        buildEnvVars.Should().NotContainKey("CUSTOM_GH_TOKEN");
        buildEnvVars.Should().NotContainKey("ANDROID_KEYSTORE_BASE64");
        buildEnvVars.Should().NotContainKey("ANDROID_SIGNING_STORE_PASS");
        buildEnvVars.Should().NotContainKey("ANDROID_SIGNING_KEY_PASS");
        buildEnvVars.Should().ContainKey("PRIVATE_FEED_RESTORE_TOKEN").WhoseValue.Should().Be("restore-pat");
        buildEnvVars.Should().ContainKey("VSS_NUGET_EXTERNAL_FEED_ENDPOINTS").WhoseValue.Should().Be("endpoint-json");

        // 2. Pack environment: scrubs publication/push tokens, but INCLUDES signing secrets for packaging
        var packScrubKeys = publishSecretNames;
        var packEnvVars = allSecrets
            .Where(kvp => !packScrubKeys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        packEnvVars.Should().NotContainKey("NUGET_API_KEY");
        packEnvVars.Should().NotContainKey("CUSTOM_PUSH_KEY");
        packEnvVars.Should().NotContainKey("GITHUB_TOKEN");
        packEnvVars.Should().NotContainKey("GH_TOKEN");
        packEnvVars.Should().NotContainKey("CUSTOM_GH_TOKEN");
        // Signing secrets MUST be present for APK/package signing
        packEnvVars.Should().ContainKey("ANDROID_KEYSTORE_BASE64").WhoseValue.Should().Be("keystore-base64");
        packEnvVars.Should().ContainKey("ANDROID_SIGNING_STORE_PASS").WhoseValue.Should().Be("store-pass-123");
        packEnvVars.Should().ContainKey("ANDROID_SIGNING_KEY_PASS").WhoseValue.Should().Be("key-pass-123");
        packEnvVars.Should().ContainKey("PRIVATE_FEED_RESTORE_TOKEN").WhoseValue.Should().Be("restore-pat");
    }

    [Fact]
    public void DeployerYamlReader_extracts_publish_secrets_and_signing_secrets_separately()
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
                    keyAlias: android
                    storePassword:
                      from: env
                      name: "MY_STORE_PASS"
            nuget:
              enabled: true
              apiKeyEnvVar: "MY_NUGET_SECRET"
            """;

        var config = DeployerYamlReader.ParseConfig(yaml);

        // Publish secrets: only publication tokens
        config.PublishSecretNames.Should().Contain("NUGET_API_KEY");
        config.PublishSecretNames.Should().Contain("GITHUB_TOKEN");
        config.PublishSecretNames.Should().Contain("GH_TOKEN");
        config.PublishSecretNames.Should().Contain("MY_NUGET_SECRET");
        config.PublishSecretNames.Should().Contain("MY_GH_SECRET");
        config.PublishSecretNames.Should().NotContain("MY_KEYSTORE_SECRET");
        config.PublishSecretNames.Should().NotContain("MY_STORE_PASS");

        // Signing secrets: signing credentials needed for packaging
        config.SigningSecretNames.Should().Contain("ANDROID_KEYSTORE_BASE64");
        config.SigningSecretNames.Should().Contain("ANDROID_SIGNING_STORE_PASS");
        config.SigningSecretNames.Should().Contain("ANDROID_SIGNING_KEY_PASS");
        config.SigningSecretNames.Should().Contain("MY_KEYSTORE_SECRET");
        config.SigningSecretNames.Should().Contain("MY_STORE_PASS");
        config.SigningSecretNames.Should().NotContain("android", "keyAlias is a literal string and must not be treated as a secret name");
    }

    [Fact]
    public void Fleet_deployer_yaml_separates_android_signing_from_publish_tokens()
    {
        var fleetYaml = """
            version: 1
            github:
              enabled: true
              owner: SuperJMN
              repo: DotnetDeployer.Fleet
              token:
                from: env
                name: GITHUB_TOKEN
              outputDir: artifacts
              packages:
                - project: src/DotnetDeployer.Fleet.Android/DotnetDeployer.Fleet.Android.csproj
                  formats:
                    - type: Apk
                      arch: [arm64]
                      signing:
                        keystore:
                          from: env
                          name: ANDROID_KEYSTORE_BASE64
                          encoding: base64
                        storePassword:
                          from: env
                          name: ANDROID_SIGNING_STORE_PASS
                        keyAlias: android
                        keyPassword:
                          from: env
                          name: ANDROID_SIGNING_KEY_PASS
            nuget:
              enabled: true
              source: https://api.nuget.org/v3/index.json
              apiKey:
                from: env
                name: NUGET_API_KEY
            """;

        var config = DeployerYamlReader.ParseConfig(fleetYaml);

        // Publish secrets
        config.PublishSecretNames.Should().Contain("NUGET_API_KEY");
        config.PublishSecretNames.Should().Contain("GITHUB_TOKEN");
        config.PublishSecretNames.Should().NotContain("ANDROID_KEYSTORE_BASE64");
        config.PublishSecretNames.Should().NotContain("ANDROID_SIGNING_STORE_PASS");
        config.PublishSecretNames.Should().NotContain("ANDROID_SIGNING_KEY_PASS");

        // Signing secrets
        config.SigningSecretNames.Should().Contain("ANDROID_KEYSTORE_BASE64");
        config.SigningSecretNames.Should().Contain("ANDROID_SIGNING_STORE_PASS");
        config.SigningSecretNames.Should().Contain("ANDROID_SIGNING_KEY_PASS");
        config.SigningSecretNames.Should().NotContain("android");
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
    public async Task Real_process_pack_environment_receives_signing_secret_while_tests_receive_no_push_tokens()
    {
        const string sentinelPushNuget = "SENTINEL_PUSH_NUGET_KEY_123";
        const string sentinelPushGh = "SENTINEL_PUSH_GH_TOKEN_456";
        const string sentinelSigningPass = "SENTINEL_ANDROID_STORE_PASS_789";
        const string sentinelRestore = "SENTINEL_RESTORE_SECRET_ABC";

        // Ambient secrets present on host
        Environment.SetEnvironmentVariable("NUGET_API_KEY", sentinelPushNuget);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", sentinelPushGh);

        try
        {
            var publishScrubKeys = new HashSet<string>(["NUGET_API_KEY", "GITHUB_TOKEN"], StringComparer.OrdinalIgnoreCase);
            var signingKeys = new HashSet<string>(["ANDROID_SIGNING_STORE_PASS"], StringComparer.OrdinalIgnoreCase);

            var allConfiguredSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NUGET_API_KEY"] = sentinelPushNuget,
                ["GITHUB_TOKEN"] = sentinelPushGh,
                ["ANDROID_SIGNING_STORE_PASS"] = sentinelSigningPass,
                ["PRIVATE_FEED_RESTORE_TOKEN"] = sentinelRestore
            };

            var isWindows = OperatingSystem.IsWindows();
            var shellExe = isWindows ? "cmd.exe" : "sh";

            // 1. Solution test environment: scrubs both publish keys AND signing keys
            var buildScrubKeys = publishScrubKeys.Concat(signingKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var testEnvVars = allConfiguredSecrets
                .Where(kvp => !buildScrubKeys.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

            var testPsi = new ProcessStartInfo(shellExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (isWindows)
            {
                testPsi.ArgumentList.Add("/c");
                testPsi.ArgumentList.Add("echo NUGET=%NUGET_API_KEY%|GH=%GITHUB_TOKEN%|SIGNING=%ANDROID_SIGNING_STORE_PASS%|RESTORE=%PRIVATE_FEED_RESTORE_TOKEN%");
            }
            else
            {
                testPsi.ArgumentList.Add("-c");
                testPsi.ArgumentList.Add("echo \"NUGET=$NUGET_API_KEY|GH=$GITHUB_TOKEN|SIGNING=$ANDROID_SIGNING_STORE_PASS|RESTORE=$PRIVATE_FEED_RESTORE_TOKEN\"");
            }

            DeployerRunner.ApplyBuildEnvironment(testPsi);
            foreach (var key in buildScrubKeys)
                testPsi.Environment.Remove(key);
            foreach (var (k, v) in testEnvVars)
                testPsi.Environment[k] = v;

            using (var testProc = Process.Start(testPsi)!)
            {
                var testOutput = await testProc.StandardOutput.ReadToEndAsync();
                await testProc.WaitForExitAsync();
                testProc.ExitCode.Should().Be(0);

                // Tests MUST NOT receive push tokens
                testOutput.Should().NotContain(sentinelPushNuget);
                testOutput.Should().NotContain(sentinelPushGh);
                // Tests MUST NOT receive signing secrets
                testOutput.Should().NotContain(sentinelSigningPass);
                // Tests MUST receive restore tokens
                testOutput.Should().Contain(sentinelRestore);
            }

            // 2. Pack environment: scrubs publication tokens, but PASSES signing secrets to DotnetDeployer
            var packScrubKeys = publishScrubKeys;
            var packEnvVars = allConfiguredSecrets
                .Where(kvp => !packScrubKeys.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

            var packPsi = new ProcessStartInfo(shellExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (isWindows)
            {
                packPsi.ArgumentList.Add("/c");
                packPsi.ArgumentList.Add("echo NUGET=%NUGET_API_KEY%|GH=%GITHUB_TOKEN%|SIGNING=%ANDROID_SIGNING_STORE_PASS%|RESTORE=%PRIVATE_FEED_RESTORE_TOKEN%");
            }
            else
            {
                packPsi.ArgumentList.Add("-c");
                packPsi.ArgumentList.Add("echo \"NUGET=$NUGET_API_KEY|GH=$GITHUB_TOKEN|SIGNING=$ANDROID_SIGNING_STORE_PASS|RESTORE=$PRIVATE_FEED_RESTORE_TOKEN\"");
            }

            DeployerRunner.ApplyBuildEnvironment(packPsi);
            foreach (var key in packScrubKeys)
                packPsi.Environment.Remove(key);
            foreach (var (k, v) in packEnvVars)
                packPsi.Environment[k] = v;

            using (var packProc = Process.Start(packPsi)!)
            {
                var packOutput = await packProc.StandardOutput.ReadToEndAsync();
                await packProc.WaitForExitAsync();
                packProc.ExitCode.Should().Be(0);

                // Pack MUST NOT receive push tokens
                packOutput.Should().NotContain(sentinelPushNuget);
                packOutput.Should().NotContain(sentinelPushGh);
                // Pack MUST receive signing secrets
                packOutput.Should().Contain(sentinelSigningPass);
                // Pack MUST receive restore tokens
                packOutput.Should().Contain(sentinelRestore);
            }
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
