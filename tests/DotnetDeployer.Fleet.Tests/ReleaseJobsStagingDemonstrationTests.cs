using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ReleaseJobsStagingDemonstrationTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    public void Dispose()
    {
        foreach (var dir in tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Scenario1_HappyPath_releases_exact_inventory_to_staging_feed()
    {
        var workDir = CreateTempDir("work");
        var stagingFeedDir = CreateTempDir("staging-feed");
        var packOutDir = CreateTempDir("nupkg");

        var pkg1 = CreateFakeNupkg(packOutDir, "Demo.ModuleA", "1.0.0");
        var pkg2 = CreateFakeNupkg(packOutDir, "Demo.ModuleB", "1.0.0");

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Deploy,
            TriggerCommitSha = "1111111111111111111111111111111111111111"
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "DemoProject",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["Demo.ModuleA", "Demo.ModuleB"]
        };

        var phasesEmitted = new List<string>();
        var logLines = new List<string>();

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: _ =>
            {
                phasesEmitted.Add("worker.solution.test");
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runPack: _ =>
            {
                phasesEmitted.Add("worker.deployer.pack");
                return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, [pkg1, pkg2]));
            },
            verifyInventory: (packages, _) =>
            {
                phasesEmitted.Add("worker.inventory.verify");
                var ids = packages.Select(NuGetPackageReader.ReadPackageId).ToList();
                var validation = PackageInventoryValidator.Validate(project.ExpectedPackageIds, ids);
                return Task.FromResult((validation.IsValid, validation.ErrorMessage));
            },
            runPush: async (packages, ct) =>
            {
                phasesEmitted.Add("worker.nuget.push");
                foreach (var pkg in packages)
                {
                    var pushResult = await NuGetPackagePusher.PushAsync(
                        workingDirectory: workDir,
                        packagePath: pkg,
                        apiKey: "staging-key",
                        source: stagingFeedDir,
                        onLine: line => { logLines.Add(line); return Task.CompletedTask; },
                        ct: ct);

                    if (!pushResult.Success)
                        return pushResult;
                }
                return (true, null);
            });

        result.Success.Should().BeTrue();
        phasesEmitted.Should().Equal(
            "worker.solution.test",
            "worker.deployer.pack",
            "worker.inventory.verify",
            "worker.nuget.push");

        // Verify staging feed contains the pushed packages
        var stagedPackages = Directory.GetFiles(stagingFeedDir, "*.nupkg", SearchOption.AllDirectories);
        stagedPackages.Select(Path.GetFileName).Should().Contain(["Demo.ModuleA.1.0.0.nupkg", "Demo.ModuleB.1.0.0.nupkg"]);
    }

    [Fact]
    public async Task Scenario2_TestFailure_halts_before_pack_and_leaves_staging_feed_empty()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-fail");
        var packInvoked = false;
        var pushInvoked = false;

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Deploy,
            TriggerCommitSha = "2222222222222222222222222222222222222222"
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "DemoProject",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["Demo.ModuleA"]
        };

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((false, "Unit test failed: expected 42 but was 0")),
            runPack: _ =>
            {
                packInvoked = true;
                return Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, []));
            },
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runPush: (_, _) =>
            {
                pushInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unit test failed");
        packInvoked.Should().BeFalse("pack must not be invoked when tests fail");
        pushInvoked.Should().BeFalse("push must not be invoked when tests fail");

        // Staging feed must remain untouched
        Directory.GetFiles(stagingFeedDir, "*.nupkg").Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario3_InventoryDiscrepancy_halts_before_push_and_leaves_staging_feed_empty()
    {
        var workDir = CreateTempDir("work-inv-disc");
        var stagingFeedDir = CreateTempDir("staging-feed-inv-disc");
        var packOutDir = CreateTempDir("nupkg-inv-disc");

        // Produce only Demo.ModuleA, but expected Demo.ModuleA AND Demo.ModuleB
        var pkg1 = CreateFakeNupkg(packOutDir, "Demo.ModuleA", "1.0.0");
        var pushInvoked = false;

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Deploy,
            TriggerCommitSha = "3333333333333333333333333333333333333333"
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "DemoProject",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["Demo.ModuleA", "Demo.ModuleB"]
        };

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((true, null)),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, [pkg1])),
            verifyInventory: (packages, _) =>
            {
                var ids = packages.Select(NuGetPackageReader.ReadPackageId).ToList();
                var validation = PackageInventoryValidator.Validate(project.ExpectedPackageIds, ids);
                return Task.FromResult((validation.IsValid, validation.ErrorMessage));
            },
            runPush: (_, _) =>
            {
                pushInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("missing: [Demo.ModuleB]");
        pushInvoked.Should().BeFalse("push must not be invoked on inventory discrepancy");

        // Staging feed must remain empty
        Directory.GetFiles(stagingFeedDir, "*.nupkg").Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario4_Cancellation_aborts_pipeline_and_leaves_staging_feed_empty()
    {
        var stagingFeedDir = CreateTempDir("staging-feed-cancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Deploy,
            TriggerCommitSha = "4444444444444444444444444444444444444444"
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "DemoProject",
            RunTestsBeforeDeploy = true,
            ExpectedPackageIds = ["Demo.ModuleA"]
        };

        var act = async () => await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<(bool, string?)>((true, null));
            },
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, [])),
            verifyInventory: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            runPush: (_, _) => Task.FromResult<(bool, string?)>((true, null)),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(stagingFeedDir, "*.nupkg").Should().BeEmpty();
    }

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }

    private static string CreateFakeNupkg(string outputDir, string packageId, string version)
    {
        var nupkgPath = Path.Combine(outputDir, $"{packageId}.{version}.nupkg");
        var nuspecXml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{packageId}</id>
                <version>{version}</version>
                <authors>TestAuthor</authors>
                <description>Test description</description>
              </metadata>
            </package>
            """;

        using (var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry($"{packageId}.nuspec");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(nuspecXml);
        }

        return nupkgPath;
    }
}
