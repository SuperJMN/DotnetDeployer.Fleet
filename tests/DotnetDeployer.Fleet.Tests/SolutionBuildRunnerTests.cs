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
        processRunner.Commands.Should().HaveCount(2);
        processRunner.Commands[0].Arguments.Should().Equal("workload", "restore", solution);
        processRunner.Commands[1].Arguments.Should().Equal(
            "build", solution, "-c", "Release", "--nologo");
        processRunner.Commands.Should().OnlyContain(command => command.WorkingDirectory == repositoryRoot);
        processRunner.Commands.Should().OnlyContain(command => command.Environment["PRIVATE_FEED_TOKEN"] == "token123");
        processRunner.Commands.Should().OnlyContain(command => !command.Environment.ContainsKey("NUGET_API_KEY"));
        lines.Should().Contain(line => line.Contains("continuing to dotnet build", StringComparison.Ordinal));
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
    public async Task Non_test_project_compilation_failure_is_caught_by_build_gate_where_test_runner_would_pass()
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

        // Create solution
        var psiNewSln = new ProcessStartInfo("dotnet", ["new", "sln", "-n", "Reproduce26", "-o", repositoryRoot])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using (var p = Process.Start(psiNewSln)!)
        {
            await p.WaitForExitAsync();
            p.ExitCode.Should().Be(0);
        }

        var slnPath = Directory.GetFiles(repositoryRoot, "Reproduce26.*")
            .First(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

        var psiAdd = new ProcessStartInfo("dotnet", [
            "sln", slnPath, "add",
            Path.Combine(brokenLibDir, "BrokenLib.csproj"),
            Path.Combine(testDir, "UnitTests.csproj")
        ])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using (var p = Process.Start(psiAdd)!)
        {
            await p.WaitForExitAsync();
            p.ExitCode.Should().Be(0);
        }

        // 1. SolutionTestRunner runs `dotnet test Reproduce26.sln -c Release --nologo -m:1`
        // Because dotnet test only builds test projects and their project references by default,
        // BrokenLib is skipped, and dotnet test exits with 0!
        var testResult = await SolutionTestRunner.RunAsync(repositoryRoot, _ => Task.CompletedTask);
        testResult.Success.Should().BeTrue("dotnet test returns 0 because BrokenLib is not referenced by tests");

        // 2. SolutionBuildRunner runs `dotnet build Reproduce26.sln -c Release --nologo`
        // dotnet build builds all projects in the solution and FAILS with exit code 1!
        var buildResult = await SolutionBuildRunner.RunAsync(repositoryRoot, _ => Task.CompletedTask);
        buildResult.Success.Should().BeFalse("dotnet build must fail because BrokenLib fails compilation");
        buildResult.Error.Should().Contain("dotnet build exited with code 1");
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
