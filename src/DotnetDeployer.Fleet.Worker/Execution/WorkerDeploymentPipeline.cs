using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

internal static class WorkerDeploymentPipeline
{
    internal static bool ShouldRunSolutionTests(DeploymentJob job, Project project) =>
        project.RunTestsBeforeDeploy;

    internal static async Task<(bool Success, string? Error)> RunAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionBuild,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runDeployer,
        CancellationToken ct = default)
    {
        var buildResult = await runSolutionBuild(ct);
        if (!buildResult.Success)
            return buildResult;

        if (ShouldRunSolutionTests(job, project))
        {
            var testResult = await runSolutionTests(ct);
            if (!testResult.Success)
                return testResult;
        }

        return await runDeployer(ct);
    }

    internal static Task<(bool Success, string? Error)> RunAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runDeployer,
        CancellationToken ct = default) =>
        RunAsync(job, project, _ => Task.FromResult<(bool, string?)>((true, null)), runSolutionTests, runDeployer, ct);

    /// <summary>
    /// Executes the release pipeline for a project:
    /// 1. Solution build gate
    /// 2. Solution test gate (if configured)
    /// 3. Package generation (pack)
    /// 4. Package inventory verification
    /// 5. NuGet package push
    /// 6. Additional publication (e.g. GitHub release)
    /// 
    /// Note: Publication across multiple targets (e.g. NuGet feed followed by GitHub release)
    /// is sequential and NOT atomic. If GitHub release fails after packages have been pushed
    /// to the NuGet feed, the pushed packages remain published in the feed (partial release).
    /// </summary>
    internal static async Task<(bool Success, string? Error)> RunReleasePipelineAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionBuild,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error, IReadOnlyList<string> ProducedPackagePaths)>> runPack,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> verifyInventory,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> runPush,
        Func<CancellationToken, Task<(bool Success, string? Error)>>? runAdditionalPublish = null,
        CancellationToken ct = default)
    {
        var buildResult = await runSolutionBuild(ct);
        if (!buildResult.Success)
            return buildResult;

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

        var pushResult = await runPush(packResult.ProducedPackagePaths, ct);
        if (!pushResult.Success)
            return pushResult;

        if (runAdditionalPublish is not null)
        {
            var additionalResult = await runAdditionalPublish(ct);
            if (!additionalResult.Success)
                return additionalResult;
        }

        return (true, null);
    }

    internal static Task<(bool Success, string? Error)> RunReleasePipelineAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error, IReadOnlyList<string> ProducedPackagePaths)>> runPack,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> verifyInventory,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> runPush,
        CancellationToken ct = default) =>
        RunReleasePipelineAsync(
            job,
            project,
            _ => Task.FromResult<(bool, string?)>((true, null)),
            runSolutionTests,
            runPack,
            verifyInventory,
            runPush,
            null,
            ct);
}
