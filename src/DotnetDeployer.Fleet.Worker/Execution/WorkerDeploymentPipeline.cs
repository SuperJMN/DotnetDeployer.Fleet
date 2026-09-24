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
        ct.ThrowIfCancellationRequested();
        var buildResult = await runSolutionBuild(ct);
        if (!buildResult.Success)
            return buildResult;

        ct.ThrowIfCancellationRequested();
        if (ShouldRunSolutionTests(job, project))
        {
            var testResult = await runSolutionTests(ct);
            if (!testResult.Success)
                return testResult;
        }

        ct.ThrowIfCancellationRequested();
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
    /// 5. NuGet push, then any additional publication target
    /// All local gates complete before either external publication begins.
    /// </summary>
    internal static async Task<(bool Success, string? Error)> RunReleasePipelineAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionBuild,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error, IReadOnlyList<string> ProducedPackagePaths)>> runPack,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> verifyInventory,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>>? runPush = null,
        Func<CancellationToken, Task<(bool Success, string? Error)>>? runAdditionalPublish = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (runPush is null && runAdditionalPublish is null)
            return (false, "Release has no enabled publication destination; check deployer.yaml before retrying.");

        var buildResult = await runSolutionBuild(ct);
        if (!buildResult.Success)
            return buildResult;

        ct.ThrowIfCancellationRequested();
        if (ShouldRunSolutionTests(job, project))
        {
            var testResult = await runSolutionTests(ct);
            if (!testResult.Success)
                return testResult;
        }

        ct.ThrowIfCancellationRequested();
        var packResult = await runPack(ct);
        if (!packResult.Success)
            return (false, packResult.Error);

        ct.ThrowIfCancellationRequested();
        var verifyResult = await verifyInventory(packResult.ProducedPackagePaths, ct);
        if (!verifyResult.Success)
            return verifyResult;

        ct.ThrowIfCancellationRequested();
        if (runPush is not null)
        {
            var pushResult = await runPush(packResult.ProducedPackagePaths, ct);
            if (!pushResult.Success)
                return pushResult;
        }

        ct.ThrowIfCancellationRequested();
        if (runAdditionalPublish is not null)
        {
            (bool Success, string? Error) additionalResult;
            try
            {
                additionalResult = await runAdditionalPublish(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (runPush is not null)
            {
                return (false, $"Partial publication: NuGet packages were pushed, but the additional publication failed: {ex.Message}");
            }
            if (!additionalResult.Success)
                return runPush is null
                    ? additionalResult
                    : (false, $"Partial publication: NuGet packages were pushed, but the additional publication failed: {additionalResult.Error}");
        }

        return (true, null);
    }

    internal static Task<(bool Success, string? Error)> RunReleasePipelineAsync(
        DeploymentJob job,
        Project project,
        Func<CancellationToken, Task<(bool Success, string? Error)>> runSolutionTests,
        Func<CancellationToken, Task<(bool Success, string? Error, IReadOnlyList<string> ProducedPackagePaths)>> runPack,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>> verifyInventory,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Success, string? Error)>>? runPush = null,
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
