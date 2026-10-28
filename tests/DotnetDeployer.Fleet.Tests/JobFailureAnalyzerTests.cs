using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Tooling.Agents;

namespace DotnetDeployer.Fleet.Tests;

public sealed class JobFailureAnalyzerTests
{
    private static JobView FailedJob(string? error = null, string? worker = "Peach") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "App", JobKind.Deploy, JobStatus.Failed, Guid.NewGuid(), worker,
            new string('a', 40), false, "1.0.0", null, DateTimeOffset.UtcNow, null, null, null, null, error);

    [Fact]
    public void Build_errors_keep_code_message_file_and_project_once_each()
    {
        string[] log =
        [
            "  /src/App/Program.cs(12,5): error CS0246: The type or namespace name 'Foo' could not be found [/src/App/App.csproj::TargetFramework=net10.0]",
            "Build FAILED.",
            "  /src/App/Program.cs(12,5): error CS0246: The type or namespace name 'Foo' could not be found [/src/App/App.csproj::TargetFramework=net10.0]",
            "    0 Warning(s)",
            "    1 Error(s)"
        ];

        var report = JobFailureAnalyzer.Analyze(FailedJob(), [], log);

        report.BuildErrors.Should().ContainSingle().Which.Should().Be(
            new BuildError("CS0246", "The type or namespace name 'Foo' could not be found", "App.csproj", "/src/App/Program.cs", 12));
        report.Origin.Should().Be(FailureOrigin.Code);
    }

    [Fact]
    public void Failed_tests_carry_their_message_and_first_stack_frames()
    {
        string[] log =
        [
            "  Passed App.Tests.Math.Adds [3 ms]",
            "  Failed App.Tests.Math.Divides [12 ms]",
            "  Error Message:",
            "   Expected 2, but found 3.",
            "  Stack Trace:",
            "     at App.Tests.Math.Divides() in /src/Tests/MathTests.cs:line 20",
            "     at System.RuntimeMethodHandle.InvokeMethod(Object target)",
            "",
            "Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2"
        ];

        var report = JobFailureAnalyzer.Analyze(FailedJob(), [], log);

        report.FailedTests.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Name = "App.Tests.Math.Divides",
            Message = "Expected 2, but found 3.",
            StackTrace = new[]
            {
                "at App.Tests.Math.Divides() in /src/Tests/MathTests.cs:line 20",
                "at System.RuntimeMethodHandle.InvokeMethod(Object target)"
            }
        });
        report.Suggestions.Should().Contain(s => s.Contains("Tests failed"));
    }

    [Fact]
    public void A_missing_android_sdk_is_an_infrastructure_failure_with_a_remedy()
    {
        string[] log =
        [
            "/usr/share/dotnet/packs/Microsoft.Android.Sdk.Linux/35.0.7/tools/Xamarin.Android.Tooling.targets(70,5): error XA5300: The Android SDK directory could not be found. [/src/App.Android/App.Android.csproj]"
        ];

        var report = JobFailureAnalyzer.Analyze(FailedJob(), [], log);

        report.Origin.Should().Be(FailureOrigin.Infrastructure);
        report.BuildErrors.Should().ContainSingle(e => e.Code == "XA5300" && e.Project == "App.Android.csproj");
        report.Suggestions.Should().Contain(s => s.Contains("ANDROID_HOME") && s.Contains("Peach"));
    }

    [Fact]
    public void Failures_from_a_lost_worker_are_infrastructure()
    {
        var report = JobFailureAnalyzer.Analyze(FailedJob("Worker unresponsive — marked as failed by stale job reaper."), [], []);

        report.Origin.Should().Be(FailureOrigin.Infrastructure);
    }

    [Fact]
    public void A_missing_secret_suggests_how_to_add_it()
    {
        var report = JobFailureAnalyzer.Analyze(FailedJob(), [], ["Environment variable 'NUGET_API_KEY' is not set"]);

        report.Suggestions.Should().Contain(s => s.Contains("fleet secrets set NUGET_API_KEY"));
    }

    [Fact]
    public void The_failed_phase_comes_from_the_phase_timeline()
    {
        PhaseView[] phases =
        [
            new("worker.git.clone", PhaseStatus.Ok, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10, null),
            new("worker.solution.test", PhaseStatus.Fail, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10, null)
        ];

        JobFailureAnalyzer.Analyze(FailedJob(), phases, []).FailedPhase.Should().Be("worker.solution.test");
    }

    [Fact]
    public void Relevant_lines_skip_summaries_without_problems_and_command_line_flags()
    {
        string[] log =
        [
            "    0 Errores",
            "Correctas! - Con error:     0, Superado:   512",
            "wasm-ld --enable-exception-handling -mllvm -exception-model=wasm",
            "9281e54\tfix: report failed GUI commands",
            "=== Deployment FAILED: Remote package conflicts ==="
        ];

        JobFailureAnalyzer.Analyze(FailedJob(), [], log).ErrorLines
            .Should().Equal("=== Deployment FAILED: Remote package conflicts ===");
    }
}
