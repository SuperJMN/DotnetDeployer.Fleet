using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class WorkerDeploymentPipeline
{
    internal static bool ShouldRunSolutionTests(DeploymentJob job, Project project) =>
        project.RunTestsBeforeDeploy;

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

    internal static async Task<(bool Success, string? Error)> RunReleasePipelineAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error, IReadOnlyList<string> ProducedPackagePaths)>> runPack,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> verifyInventory,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> runPush,
        CancellationToken ct = default)
    {
        if (ShouldRunSolutionTests(job, project))
        {
            var testResult = await runSolutionTests(ct);
            if (!testResult.Success)
                return testResult;
        }

        var packResult = await runPack(ct);
        if (!packResult.Success)
            return (false, packResult.Error);

        var verifyResult = await verifyInventory(packResult.ProducedPackagePaths, ct);
        if (!verifyResult.Success)
            return verifyResult;

        return await runPush(packResult.ProducedPackagePaths, ct);
    }
}
