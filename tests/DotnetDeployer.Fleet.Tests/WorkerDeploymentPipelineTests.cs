using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;

namespace DotnetDeployer.Fleet.Tests;

public sealed class WorkerDeploymentPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_and_automatic_deploys_run_tests_before_deployer(bool isAutoTriggered)
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy, IsAutoTriggered = isAutoTriggered };
        var project = new Project { RunTestsBeforeDeploy = true };
        var calls = new List<string>();

        var result = await WorkerDeploymentPipeline.RunAsync(
            job,
            project,
            _ =>
            {
                calls.Add("tests");
                return Task.FromResult<(bool, string?)>((true, null));
            },
            _ =>
            {
                calls.Add("deployer");
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeTrue();
        calls.Should().Equal("tests", "deployer");
    }

    [Fact]
    public async Task Failed_tests_block_DotnetDeployer()
    {
        var deployerCalls = 0;

        var result = await WorkerDeploymentPipeline.RunAsync(
            new DeploymentJob { Kind = JobKind.Deploy },
            new Project { RunTestsBeforeDeploy = true },
            _ => Task.FromResult<(bool, string?)>((false, "tests failed")),
            _ =>
            {
                deployerCalls++;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("tests failed");
        deployerCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(JobKind.Deploy, true)]
    [InlineData(JobKind.PackageBuild, true)]
    public async Task Both_deploy_and_package_build_run_solution_tests_when_enabled(JobKind kind, bool isAutoTriggered)
    {
        var job = new DeploymentJob { Kind = kind, IsAutoTriggered = isAutoTriggered };
        var project = new Project { RunTestsBeforeDeploy = true };
        var calls = new List<string>();

        var result = await WorkerDeploymentPipeline.RunAsync(
            job,
            project,
            _ =>
            {
                calls.Add("tests");
                return Task.FromResult<(bool, string?)>((true, null));
            },
            _ =>
            {
                calls.Add("deployer");
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeTrue();
        calls.Should().Equal("tests", "deployer");
    }

    [Theory]
    [InlineData(JobKind.Deploy)]
    [InlineData(JobKind.PackageBuild)]
    public async Task Disabled_projects_skip_solution_tests(JobKind kind)
    {
        var testCalls = 0;
        var deployerCalls = 0;

        var result = await WorkerDeploymentPipeline.RunAsync(
            new DeploymentJob { Kind = kind },
            new Project { RunTestsBeforeDeploy = false },
            _ =>
            {
                testCalls++;
                return Task.FromResult<(bool, string?)>((true, null));
            },
            _ =>
            {
                deployerCalls++;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeTrue();
        testCalls.Should().Be(0);
        deployerCalls.Should().Be(1);
    }

    [Fact]
    public async Task RunReleasePipelineAsync_happy_path_runs_tests_pack_verify_and_push()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var calls = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            _ => { calls.Add("tests"); return Task.FromResult<(bool, string?)>((true, null)); },
            _ => { calls.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])); },
            (pkgs, _) => { calls.Add($"verify:{pkgs.Count}"); return Task.FromResult<(bool, string?)>((true, null)); },
            (pkgs, _) => { calls.Add($"push:{pkgs.Count}"); return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeTrue();
        calls.Should().Equal("tests", "pack", "verify:1", "push:1");
    }

    [Fact]
    public async Task RunReleasePipelineAsync_test_failure_halts_before_pack_verify_push()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var calls = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            _ => { calls.Add("tests"); return Task.FromResult<(bool, string?)>((false, "tests failed")); },
            _ => { calls.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, [])); },
            (_, _) => { calls.Add("verify"); return Task.FromResult<(bool, string?)>((true, null)); },
            (_, _) => { calls.Add("push"); return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("tests failed");
        calls.Should().Equal("tests");
    }

    [Fact]
    public async Task RunReleasePipelineAsync_pack_failure_halts_before_verify_or_push()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var calls = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            _ => { calls.Add("tests"); return Task.FromResult<(bool, string?)>((true, null)); },
            _ => { calls.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((false, "pack error", [])); },
            (_, _) => { calls.Add("verify"); return Task.FromResult<(bool, string?)>((true, null)); },
            (_, _) => { calls.Add("push"); return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("pack error");
        calls.Should().Equal("tests", "pack");
    }

    [Fact]
    public async Task RunReleasePipelineAsync_verify_failure_halts_before_push()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var calls = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            _ => { calls.Add("tests"); return Task.FromResult<(bool, string?)>((true, null)); },
            _ => { calls.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])); },
            (_, _) => { calls.Add("verify"); return Task.FromResult<(bool, string?)>((false, "inventory mismatch")); },
            (_, _) => { calls.Add("push"); return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("inventory mismatch");
        calls.Should().Equal("tests", "pack", "verify");
    }
}
