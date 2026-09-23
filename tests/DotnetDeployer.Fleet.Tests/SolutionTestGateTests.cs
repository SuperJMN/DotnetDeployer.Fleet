using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SolutionTestGateTests
{
    [Theory]
    [InlineData(JobKind.Deploy)]
    [InlineData(JobKind.PackageBuild)]
    public async Task Solution_build_is_mandatory_for_both_deploy_and_package_build(JobKind kind)
    {
        var job = new DeploymentJob { Kind = kind };
        var project = new Project { RunTestsBeforeDeploy = true };
        var stagesExecuted = new List<string>();

        var result = await WorkerDeploymentPipeline.RunAsync(
            job,
            project,
            runSolutionBuild: _ =>
            {
                stagesExecuted.Add("build");
                return Task.FromResult<(bool, string?)>((true, null));
            },
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
        stagesExecuted.Should().Equal("build", "tests", "deployer");
    }

    [Theory]
    [InlineData(JobKind.Deploy)]
    [InlineData(JobKind.PackageBuild)]
    public async Task Build_failure_halts_pipeline_without_invoking_tests_or_deployer(JobKind kind)
    {
        var job = new DeploymentJob { Kind = kind };
        var project = new Project { RunTestsBeforeDeploy = true };
        var testsInvoked = false;
        var deployerInvoked = false;

        var result = await WorkerDeploymentPipeline.RunAsync(
            job,
            project,
            runSolutionBuild: _ => Task.FromResult<(bool, string?)>((false, "dotnet build exited with code 1")),
            runSolutionTests: _ =>
            {
                testsInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runDeployer: _ =>
            {
                deployerInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("dotnet build exited with code 1");
        testsInvoked.Should().BeFalse("tests must NEVER run if solution build fails");
        deployerInvoked.Should().BeFalse("deployer must NEVER run if solution build fails");
    }

    [Fact]
    public async Task Build_cancellation_halts_pipeline()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var testsInvoked = false;

        var act = async () => await WorkerDeploymentPipeline.RunAsync(
            job,
            project,
            runSolutionBuild: ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runSolutionTests: _ =>
            {
                testsInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runDeployer: _ => Task.FromResult<(bool, string?)>((true, null)),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        testsInvoked.Should().BeFalse();
    }

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
    public async Task ReleasePipeline_build_failure_halts_without_invoking_tests_pack_verify_or_push()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true };
        var testsInvoked = false;
        var packInvoked = false;
        var verifyInvoked = false;
        var pushInvoked = false;

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionBuild: _ => Task.FromResult<(bool, string?)>((false, "compilation error")),
            runSolutionTests: _ =>
            {
                testsInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            },
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
        result.Error.Should().Be("compilation error");
        testsInvoked.Should().BeFalse("tests must not be called after build failure");
        packInvoked.Should().BeFalse("pack must not be called after build failure");
        verifyInvoked.Should().BeFalse("verify must not be called after build failure");
        pushInvoked.Should().BeFalse("push must not be called after build failure");
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

    [Fact]
    public async Task Mixed_deployment_executes_build_test_pack_verify_nuget_and_github_in_order()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["Pkg.A"] };
        var stages = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionBuild: _ => { stages.Add("build"); return Task.FromResult<(bool, string?)>((true, null)); },
            runSolutionTests: _ => { stages.Add("tests"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPack: _ => { stages.Add("pack"); return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])); },
            verifyInventory: (_, _) => { stages.Add("verify"); return Task.FromResult<(bool, string?)>((true, null)); },
            runPush: (_, _) => { stages.Add("nuget.push"); return Task.FromResult<(bool, string?)>((true, null)); },
            runAdditionalPublish: _ => { stages.Add("github.deploy"); return Task.FromResult<(bool, string?)>((true, null)); });

        result.Success.Should().BeTrue();
        stages.Should().Equal("build", "tests", "pack", "verify", "nuget.push", "github.deploy");
    }

    [Fact]
    public async Task Mixed_deployment_halts_at_nuget_failure_without_deploying_github()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project { RunTestsBeforeDeploy = true, ExpectedPackageIds = ["Pkg.A"] };
        var githubInvoked = false;

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionBuild: _ => Task.FromResult<(bool, string?)>((true, null)),
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((true, null)),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["pkg.nupkg"])),
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runPush: (_, _) => Task.FromResult<(bool, string?)>((false, "nuget 403 forbidden")),
            runAdditionalPublish: _ =>
            {
                githubInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("nuget 403 forbidden");
        githubInvoked.Should().BeFalse("GitHub deployment must NEVER run if NuGet push fails");
    }
}
