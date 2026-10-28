using System.Diagnostics;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SolutionBuildRunnerTests : IDisposable
{
    private readonly string repositoryRoot = Path.Combine(
        Path.GetTempPath(),
        $"fleet-build-tests-{Guid.NewGuid():N}");

    public SolutionBuildRunnerTests()
    {
        Directory.CreateDirectory(repositoryRoot);
    }

    [Fact]
    public async Task Runs_best_effort_workload_restore_then_release_build_with_build_environment()
    {
        var solution = Path.Combine(repositoryRoot, "App.slnx");
        File.WriteAllText(solution, string.Empty);
        var processRunner = new RecordingProcessRunner(restoreExitCode: 9, buildExitCode: 0);
        var lines = new List<string>();
        var secrets = new Dictionary<string, string> { ["PRIVATE_FEED_TOKEN"] = "token123" };
        var scrubKeys = new[] { "NUGET_API_KEY", "GITHUB_TOKEN" };

        var result = await SolutionBuildRunner.RunAsync(
            repositoryRoot,
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            },
            secrets,
            scrubKeys,
            processRunner);

        result.Success.Should().BeTrue();
        processRunner.Commands.Should().HaveCount(3);
        processRunner.Commands[0].Arguments.Take(6).Should().Equal(
            "dnx", "dotnetdeployer.tool", "-y", "prepare", "--directory", repositoryRoot);
        processRunner.Commands[1].Arguments.Should().Equal("workload", "restore", solution);
        processRunner.Commands[2].Arguments.Should().Equal(
            "build", solution, "-c", "Release", "--nologo");
        processRunner.Commands.Should().OnlyContain(command => command.WorkingDirectory == repositoryRoot);
        processRunner.Commands.Should().OnlyContain(command => command.Environment["PRIVATE_FEED_TOKEN"] == "token123");
        processRunner.Commands.Should().OnlyContain(command => !command.Environment.ContainsKey("NUGET_API_KEY"));
        lines.Should().Contain(line => line.Contains("continuing to dotnet build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepared_toolchain_reaches_workload_restore_and_build()
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "App.slnx"), string.Empty);
        var processRunner = new RecordingProcessRunner(restoreExitCode: 0, buildExitCode: 0)
        {
            Toolchain = new Dictionary<string, string>
            {
                ["JAVA_HOME"] = "/home/fleet/.dotnet-android-jdk/jdk-17",
                ["ANDROID_HOME"] = "/home/fleet/.android-sdk"
            }
        };

        var result = await SolutionBuildRunner.RunAsync(
            repositoryRoot,
            _ => Task.CompletedTask,
            envVars: null,
            scrubKeys: null,
            processRunner);

        result.Success.Should().BeTrue();
        processRunner.Commands.Skip(1).Should().OnlyContain(command =>
            command.Environment["JAVA_HOME"] == "/home/fleet/.dotnet-android-jdk/jdk-17"
            && command.Environment["ANDROID_HOME"] == "/home/fleet/.android-sdk");
    }

    [Fact]
    public async Task Failing_prepare_warns_and_still_builds()
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "App.slnx"), string.Empty);
        var processRunner = new RecordingProcessRunner(restoreExitCode: 0, buildExitCode: 0) { PrepareExitCode = 1 };
        var lines = new List<string>();

        var result = await SolutionBuildRunner.RunAsync(
            repositoryRoot,
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            },
            envVars: null,
            scrubKeys: null,
            processRunner);

        result.Success.Should().BeTrue();
        processRunner.Commands.Should().HaveCount(3);
        lines.Should().Contain(line => line.Contains("continuing without prepared toolchains", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failing_dotnet_build_returns_a_failed_gate_result()
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "App.sln"), string.Empty);
        var processRunner = new RecordingProcessRunner(restoreExitCode: 0, buildExitCode: 1);

        var result = await SolutionBuildRunner.RunAsync(
            repositoryRoot,
            _ => Task.CompletedTask,
            envVars: null,
            scrubKeys: null,
            processRunner);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("dotnet build exited with code 1");
    }

    [Fact]
    public async Task Process_start_exception_returns_infrastructure_error()
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "App.sln"), string.Empty);
        var processRunner = new ThrowingProcessRunner(new InvalidOperationException("Binary not found"));

        var result = await SolutionBuildRunner.RunAsync(
            repositoryRoot,
            _ => Task.CompletedTask,
            envVars: null,
            scrubKeys: null,
            processRunner);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Could not run dotnet build: Binary not found");
    }

    [Fact]
    public async Task Cancellation_during_build_rethrows_OperationCanceledException()
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "App.sln"), string.Empty);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await SolutionBuildRunner.RunAsync(
            repositoryRoot,
            _ => Task.CompletedTask,
            envVars: null,
            scrubKeys: null,
            StreamingProcessRunner.Instance,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Non_test_project_compilation_failure_fails_the_build_gate()
    {
        // Reproduce issue #26:
        // A solution with:
        // 1. BrokenLib.csproj: does NOT compile (invalid C# syntax).
        // 2. UnitTests.csproj: a test project with 1 passing test, but does NOT reference BrokenLib.
        // dotnet test on the solution does NOT build unreferenced non-test projects! It returns 0!
        // dotnet build on the solution builds ALL projects in the solution, and returns 1!

        var brokenLibDir = Path.Combine(repositoryRoot, "src", "BrokenLib");
        var testDir = Path.Combine(repositoryRoot, "tests", "UnitTests");
        Directory.CreateDirectory(brokenLibDir);
        Directory.CreateDirectory(testDir);

        File.WriteAllText(Path.Combine(brokenLibDir, "BrokenLib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(brokenLibDir, "Broken.cs"), """
            namespace BrokenLib;
            public class Broken {
                THIS IS INVALID C# SYNTAX THAT WILL NOT COMPILE;
            }
            """);

        File.WriteAllText(Path.Combine(testDir, "UnitTests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(testDir, "Tests.cs"), """
            using Xunit;
            namespace UnitTests;
            public class Tests {
                [Fact]
                public void PassingTest() => Assert.True(true);
            }
            """);

        // The solution is written directly: it does not depend on the template engine or other
        // shared state under the user's home, only on what this test puts in its own directory.
        File.WriteAllText(Path.Combine(repositoryRoot, "Reproduce26.slnx"), """
            <Solution>
              <Project Path="src/BrokenLib/BrokenLib.csproj" />
              <Project Path="tests/UnitTests/UnitTests.csproj" />
            </Solution>
            """);

        // dotnet build builds every project in the solution, not only the tests and their references.
        var output = new List<string>();
        var buildResult = await SolutionBuildRunner.RunAsync(repositoryRoot, line =>
        {
            lock (output) output.Add(line);
            return Task.CompletedTask;
        });

        var buildLog = string.Join(Environment.NewLine, output);
        buildResult.Success.Should().BeFalse($"dotnet build must fail because BrokenLib fails compilation. Build output:{Environment.NewLine}{buildLog}");
        buildResult.Error.Should().Contain("dotnet build exited with code 1", $"the build must reach compilation and fail there. Build output:{Environment.NewLine}{buildLog}");
        buildLog.Should().Contain("BrokenLib", "the compilation error comes from the broken project");
    }

    public void Dispose()
    {
        try { Directory.Delete(repositoryRoot, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingProcessRunner : IStreamingProcessRunner
    {
        private readonly Queue<int> exitCodes;

        public RecordingProcessRunner(params int[] exitCodes)
        {
            this.exitCodes = new Queue<int>(exitCodes);
        }

        public RecordingProcessRunner(int restoreExitCode, int buildExitCode)
            : this([restoreExitCode, buildExitCode])
        {
        }

        public List<CapturedCommand> Commands { get; } = [];
        public Dictionary<string, string> Toolchain { get; init; } = [];
        public int PrepareExitCode { get; init; }

        public async Task<int> RunAsync(
            ProcessStartInfo startInfo,
            Func<string, Task> onLine,
            CancellationToken ct = default)
        {
            Commands.Add(new CapturedCommand(
                startInfo.WorkingDirectory,
                startInfo.ArgumentList.ToList(),
                startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty)));
            await onLine($"process output {Commands.Count}");

            if (startInfo.ArgumentList.Contains("prepare"))
            {
                var envFile = startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("--env-file") + 1];
                File.WriteAllLines(envFile, Toolchain.Select(pair => $"{pair.Key}={pair.Value}"));
                return PrepareExitCode;
            }

            return exitCodes.Dequeue();
        }
    }

    private sealed class ThrowingProcessRunner(Exception exception) : IStreamingProcessRunner
    {
        public Task<int> RunAsync(ProcessStartInfo startInfo, Func<string, Task> onLine, CancellationToken ct = default) =>
            throw exception;
    }

    private sealed record CapturedCommand(
        string WorkingDirectory,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);
}
