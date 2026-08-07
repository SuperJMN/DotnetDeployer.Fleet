using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class WorkerDeploymentPipeline
{
    internal static bool ShouldRunSolutionTests(DeploymentJob job, Project project) =>
        job.Kind == JobKind.Deploy && project.RunTestsBeforeDeploy;

    internal static async Task<(bool Success, string? Error)> RunAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runDeployer,
        CancellationToken ct = default)
    {
        if (ShouldRunSolutionTests(job, project))
        {
            var testResult = await runSolutionTests(ct);
            if (!testResult.Success)
                return testResult;
        }

        return await runDeployer(ct);
    }
}
