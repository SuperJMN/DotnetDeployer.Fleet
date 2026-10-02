using System.Diagnostics;
using DotnetDeployer.Fleet.WorkerService.Execution;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SolutionTestRunnerTests : IDisposable
{
    private readonly string repositoryRoot = Path.Combine(
        Path.GetTempPath(),
        $"fleet-solution-tests-{Guid.NewGuid():N}");

    public SolutionTestRunnerTests()
    {
        Directory.CreateDirectory(repositoryRoot);
    }

    [Theory]
    [InlineData("App.slnx")]
    [InlineData("App.sln")]
    [InlineData("App.SLNX")]
    public void Discovers_exactly_one_supported_root_solution(string fileName)
    {
        var expected = Path.Combine(repositoryRoot, fileName);
        File.WriteAllText(expected, string.Empty);

        var solution = SolutionTestRunner.DiscoverRootSolution(repositoryRoot);

        solution.Should().Be(expected);
    }

    [Fact]
    public void Missing_solution_reports_candidates_and_opt_out()
    {
        var act = () => SolutionTestRunner.DiscoverRootSolution(repositoryRoot);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*found 0*Candidates: (none)*disable 'Run solution tests before deployment'*");
    }

    [Theory]
    [InlineData(new[] { "App.sln" }, "App.sln")]
    [InlineData(new[] { "App.slnx" }, "App.slnx")]
    [InlineData(new[] { "Legacy.sln", "Modern.slnx" }, "Modern.slnx")]
    public void Unambiguous_root_solution_is_chosen_preferring_slnx(string[] files, string expected)
    {
        foreach (var file in files)
            File.WriteAllText(Path.Combine(repositoryRoot, file), string.Empty);

        var solution = SolutionDiscovery.Discover(repositoryRoot);

        Path.GetFileName(solution.Path).Should().Be(expected);
        solution.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Slnx_next_to_sln_explains_the_preference()
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "PokemonBattleEngine.sln"), string.Empty);
        File.WriteAllText(Path.Combine(repositoryRoot, "PokemonBattleEngine.slnx"), string.Empty);

        SolutionDiscovery.Discover(repositoryRoot).Reason
            .Should().Be(".slnx preferred over PokemonBattleEngine.sln");
    }

    [Theory]
    [InlineData(new[] { "A.slnx", "B.slnx" }, "*at most one .slnx*found 2*A.slnx, B.slnx*")]
    [InlineData(new[] { "A.slnx", "B.slnx", "C.sln" }, "*at most one .slnx*found 3*A.slnx, B.slnx, C.sln*")]
    [InlineData(new[] { "A.sln", "B.sln" }, "*exactly one .slnx or .sln*found 2*A.sln, B.sln*")]
    public void Ambiguous_root_solutions_report_every_candidate(string[] files, string message)
    {
        foreach (var file in files)
            File.WriteAllText(Path.Combine(repositoryRoot, file), string.Empty);

        var act = () => SolutionTestRunner.DiscoverRootSolution(repositoryRoot);

        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public async Task Runs_best_effort_workload_restore_then_release_tests_with_build_environment()
    {
        var solution = Path.Combine(repositoryRoot, "App.slnx");
        File.WriteAllText(solution, string.Empty);
        var processRunner = new RecordingProcessRunner(restoreExitCode: 9, testExitCode: 0);
        var lines = new List<string>();
        var secrets = new Dictionary<string, string> { ["DEPLOY_TOKEN"] = "secret-value" };

        var result = await SolutionTestRunner.RunAsync(
            repositoryRoot,
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            },
            secrets,
            processRunner);

        result.Success.Should().BeTrue();
        processRunner.Commands.Should().HaveCount(2);
        processRunner.Commands[0].Arguments.Should().Equal("workload", "restore", solution);
        processRunner.Commands[1].Arguments.Should().Equal(
            "test", solution, "-c", "Release", "--nologo", "-m:1");
        processRunner.Commands.Should().OnlyContain(command => command.WorkingDirectory == repositoryRoot);
        processRunner.Commands.Should().OnlyContain(command => command.Environment["DEPLOY_TOKEN"] == "secret-value");
        processRunner.Commands.Should().OnlyContain(command => command.Environment["UseSharedCompilation"] == "false");
        lines.Should().ContainInOrder("process output 1", "process output 2");
        lines.Should().Contain(line => line.Contains("continuing to dotnet test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failing_dotnet_test_returns_a_failed_gate_result()
    {
        File.WriteAllText(Path.Combine(repositoryRoot, "App.sln"), string.Empty);
        var processRunner = new RecordingProcessRunner(restoreExitCode: 0, testExitCode: 7);

        var result = await SolutionTestRunner.RunAsync(
            repositoryRoot,
            _ => Task.CompletedTask,
            envVars: null,
            processRunner);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("dotnet test exited with code 7");
    }

    public void Dispose()
    {
        try { Directory.Delete(repositoryRoot, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingProcessRunner(params int[] exitCodes) : IStreamingProcessRunner
    {
        private readonly Queue<int> exitCodes = new(exitCodes);

        public RecordingProcessRunner(int restoreExitCode, int testExitCode)
            : this([restoreExitCode, testExitCode])
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

    private sealed record CapturedCommand(
        string WorkingDirectory,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);
}
