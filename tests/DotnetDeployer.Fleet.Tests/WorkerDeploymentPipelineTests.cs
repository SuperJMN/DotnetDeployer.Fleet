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
    [InlineData(JobKind.Deploy, false)]
    [InlineData(JobKind.PackageBuild, true)]
    [InlineData(JobKind.PackageBuild, false)]
    public async Task Disabled_projects_and_package_builds_skip_solution_tests(
        JobKind kind,
        bool runTestsBeforeDeploy)
    {
        var testCalls = 0;
        var deployerCalls = 0;

        var result = await WorkerDeploymentPipeline.RunAsync(
            new DeploymentJob { Kind = kind },
            new Project { RunTestsBeforeDeploy = runTestsBeforeDeploy },
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
}
