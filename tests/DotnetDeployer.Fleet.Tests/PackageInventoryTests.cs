using System.IO.Compression;
using System.Text;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class PackageInventoryTests : IDisposable
{
    private readonly List<string> tempFiles = [];

    public void Dispose()
    {
        foreach (var file in tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }

    [Fact]
    public void NuGetPackageReader_reads_package_id_from_nupkg()
    {
        var nupkgPath = CreateFakeNupkg("SuperJMN.AwesomeLib", "1.0.0");
        var packageId = NuGetPackageReader.ReadPackageId(nupkgPath);

        packageId.Should().Be("SuperJMN.AwesomeLib");
    }

    [Fact]
    public void NuGetPackageReader_throws_when_nuspec_missing()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"invalid-{Guid.NewGuid():N}.nupkg");
        tempFiles.Add(tempFile);

        using (var archive = ZipFile.Open(tempFile, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("not-a-nuspec.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("hello");
        }

        var act = () => NuGetPackageReader.ReadPackageId(tempFile);
        act.Should().Throw<InvalidOperationException>().WithMessage("*No .nuspec file found*");
    }

    [Fact]
    public void PackageInventoryValidator_exact_match_succeeds()
    {
        var expected = new[] { "Package.Alpha", "Package.Beta" };
        var produced = new[] { "Package.Alpha", "Package.Beta" };

        var result = PackageInventoryValidator.Validate(expected, produced);

        result.IsValid.Should().BeTrue();
        result.MissingIds.Should().BeEmpty();
        result.ExtraIds.Should().BeEmpty();
        result.DuplicateIds.Should().BeEmpty();
    }

    [Fact]
    public void PackageInventoryValidator_is_case_insensitive_following_nuget_semantics()
    {
        var expected = new[] { "PACKAGE.ALPHA", "package.beta" };
        var produced = new[] { "package.alpha", "PACKAGE.BETA" };

        var result = PackageInventoryValidator.Validate(expected, produced);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PackageInventoryValidator_empty_expected_policy_fails_closed()
    {
        var expected = Array.Empty<string>();
        var produced = new[] { "Package.Alpha" };

        var result = PackageInventoryValidator.Validate(expected, produced);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No expected package inventory policy configured");
    }

    [Fact]
    public void PackageInventoryValidator_missing_packages_blocks_push()
    {
        var expected = new[] { "Package.Alpha", "Package.Beta" };
        var produced = new[] { "Package.Alpha" };

        var result = PackageInventoryValidator.Validate(expected, produced);

        result.IsValid.Should().BeFalse();
        result.MissingIds.Should().Equal("Package.Beta");
        result.ErrorMessage.Should().Contain("missing: [Package.Beta]");
    }

    [Fact]
    public void PackageInventoryValidator_extra_packages_blocks_push()
    {
        var expected = new[] { "Package.Alpha" };
        var produced = new[] { "Package.Alpha", "Package.Unexpected" };

        var result = PackageInventoryValidator.Validate(expected, produced);

        result.IsValid.Should().BeFalse();
        result.ExtraIds.Should().Equal("Package.Unexpected");
        result.ErrorMessage.Should().Contain("extra: [Package.Unexpected]");
    }

    [Fact]
    public void PackageInventoryValidator_duplicate_packages_blocks_push()
    {
        var expected = new[] { "Package.Alpha" };
        var produced = new[] { "Package.Alpha", "package.alpha" };

        var result = PackageInventoryValidator.Validate(expected, produced);

        result.IsValid.Should().BeFalse();
        result.DuplicateIds.Should().Contain("Package.Alpha");
        result.ErrorMessage.Should().Contain("duplicate");
    }

    [Fact]
    public async Task ReleasePipeline_inventory_mismatch_blocks_push()
    {
        var job = new DeploymentJob { Kind = JobKind.Deploy };
        var project = new Project
        {
            RunTestsBeforeDeploy = false,
            ExpectedPackageIds = ["Package.A", "Package.B"]
        };
        var pushInvoked = false;

        var result = await WorkerDeploymentPipeline.RunReleasePipelineAsync(
            job,
            project,
            runSolutionTests: _ => Task.FromResult<(bool, string?)>((true, null)),
            runPack: _ => Task.FromResult<(bool, string?, IReadOnlyList<string>)>((true, null, ["Package.A.1.0.0.nupkg"])),
            verifyInventory: (packages, _) =>
            {
                // Produced package A, but expected A and B
                var produced = new[] { "Package.A" };
                var val = PackageInventoryValidator.Validate(project.ExpectedPackageIds, produced);
                return Task.FromResult((val.IsValid, val.ErrorMessage));
            },
            runPush: (_, _) =>
            {
                pushInvoked = true;
                return Task.FromResult<(bool, string?)>((true, null));
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("missing: [Package.B]");
        pushInvoked.Should().BeFalse("push must NEVER run when package inventory mismatches");
    }

    private string CreateFakeNupkg(string packageId, string version)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{packageId}.{version}.nupkg");
        tempFiles.Add(tempFile);

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

        using (var archive = ZipFile.Open(tempFile, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry($"{packageId}.nuspec");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(nuspecXml);
        }

        return tempFile;
    }
}
