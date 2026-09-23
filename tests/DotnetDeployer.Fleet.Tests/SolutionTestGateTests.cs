using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SolutionTestGateTests
{
    [Theory]
    [InlineData(JobKind.Deploy)]
    [InlineData(JobKind.PackageBuild)]
    public async Task Solution_tests_are_mandatory_for_both_deploy_and_package_build(JobKind kind)
    {
        var job = new DeploymentJob { Kind = kind };
        var project = new Project { RunTestsBeforeDeploy = true };
        var stagesExecuted = new List<string>();

        var result = await WorkerDeploymentPipeline.RunAsync(
            job,
            project,
            runSolutionTests: _ =>
            {
                stagesExecuted.Add("tests");
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runDeployer: _ =>
            {
                stagesExecuted.Add("deployer");
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeTrue();
        stagesExecuted.Should().Equal("tests", "deployer");
    }

    [Theory]
    [InlineData(JobKind.Deploy)]
    [InlineData(JobKind.PackageBuild)]
    public async Task Test_failure_halts_pipeline_without_invoking_deployer(JobKind kind)
    {
        var job = new DeploymentJob { Kind = kind };
        var project = new Project { RunTestsBeforeDeploy = true };
        var deployerInvoked = false;

        var result = await WorkerDeploymentPipeline.RunAsync(
            job,
            project,
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((false, "1 test failed: Assert.Equal()")),
            runDeployer: _ =>
            {
                deployerInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("1 test failed");
        deployerInvoked.Should().BeFalse("deployer must NEVER run if solution tests fail");
    }

    [Fact]
    public async Task ReleasePipeline_test_failure_halts_without_invoking_pack_verify_or_push()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var packInvoked = false;
        var verifyInvoked = false;
        var pushInvoked = false;

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((false, "test suite failed")),
            runPack: _ =>
            {
                packInvoked = true;
                return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["test.nupkg"]));
            },
            verifyInventory: (_, _) =>
            {
                verifyInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runPush: (_, _) =>
            {
                pushInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("test suite failed");
        packInvoked.Should().BeFalse("pack must not be called after test failure");
        verifyInvoked.Should().BeFalse("verify must not be called after test failure");
        pushInvoked.Should().BeFalse("push must not be called after test failure");
    }

    [Fact]
    public async Task ReleasePipeline_test_cancellation_halts_pipeline()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var packInvoked = false;

        var act = async () => await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runPack: _ =>
            {
                packInvoked = true;
                return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, []));
            },
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runPush: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        packInvoked.Should().BeFalse();
    }
}
