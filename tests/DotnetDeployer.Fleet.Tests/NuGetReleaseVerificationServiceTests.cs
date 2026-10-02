using System.IO.Compression;
using System.Text;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.Feeds;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DotnetDeployer.Fleet.Tests;

public sealed class NuGetReleaseVerificationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fleet-release-verify-" + Guid.NewGuid().ToString("N"));
    private string Feed => Path.Combine(root, "feed");

    [Fact]
    public async Task Indexed_identical_package_marks_succeeded_job_verified()
    {
        var (job, releases, service) = await ArrangeAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        await File.WriteAllBytesAsync(Path.Combine(Feed, "DemoLib.1.0.0.nupkg"), MakePackage("pushed"));

        await service.VerifyPendingAsync(DateTimeOffset.UtcNow);

        job.Status.Should().Be(JobStatus.Succeeded);
        job.NuGetVerification.Should().Be(NuGetVerificationStatus.Verified);
        (await releases.GetAsync(job.ProjectId, job.TriggerCommitSha!))!.Progress.Single().State
            .Should().Be(NuGetReleasePackageState.Complete);
    }

    [Fact]
    public async Task Package_still_indexing_keeps_job_succeeded_and_pending()
    {
        var (job, _, service) = await ArrangeAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        await service.VerifyPendingAsync(DateTimeOffset.UtcNow);

        job.Status.Should().Be(JobStatus.Succeeded);
        job.NuGetVerification.Should().Be(NuGetVerificationStatus.Pending);
        job.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Downloadable_package_that_differs_from_the_push_fails_the_job()
    {
        var (job, releases, service) = await ArrangeAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        await File.WriteAllBytesAsync(Path.Combine(Feed, "DemoLib.1.0.0.nupkg"), MakePackage("someone else's bytes"));

        await service.VerifyPendingAsync(DateTimeOffset.UtcNow);

        job.Status.Should().Be(JobStatus.Failed);
        job.NuGetVerification.Should().Be(NuGetVerificationStatus.Failed);
        job.ErrorMessage.Should().Contain("differs from the package Fleet pushed");
        (await releases.GetAsync(job.ProjectId, job.TriggerCommitSha!))!.Progress.Single().State
            .Should().Be(NuGetReleasePackageState.InterventionRequired);
    }

    [Fact]
    public async Task Package_that_never_becomes_downloadable_fails_after_the_timeout()
    {
        var (job, _, service) = await ArrangeAsync(DateTimeOffset.UtcNow.AddHours(-7));

        await service.VerifyPendingAsync(DateTimeOffset.UtcNow);

        job.Status.Should().Be(JobStatus.Failed);
        job.NuGetVerification.Should().Be(NuGetVerificationStatus.Failed);
        job.ErrorMessage.Should().Contain("still not downloadable");
    }

    private async Task<(DeploymentJob Job, NuGetReleaseStore Releases, NuGetReleaseVerificationService Service)> ArrangeAsync(
        DateTimeOffset finishedAt)
    {
        Directory.CreateDirectory(Feed);
        var projectId = Guid.NewGuid();
        var sha = new string('e', 40);
        var staged = Path.Combine(root, "DemoLib.nupkg");
        await File.WriteAllBytesAsync(staged, MakePackage("pushed"));
        var identity = await NuGetFeedVerifier.ReadIdentityAsync(staged, CancellationToken.None);

        var releases = new NuGetReleaseStore(Path.Combine(root, "releases"));
        await using (var bytes = File.OpenRead(staged))
            await releases.UploadPackageAsync(projectId, sha, "DemoLib", bytes);
        await releases.CreateAsync(new NuGetReleaseManifest(projectId, sha, "1.0.0", Feed, "NUGET_API_KEY", false,
            [new NuGetReleasePackage("DemoLib", "1.0.0", "packages/DemoLib.nupkg", identity.Sha256, identity.ContentHash)],
            finishedAt.AddMinutes(-1)));
        await releases.SetStateAsync(projectId, sha, "DemoLib", NuGetReleasePackageState.AwaitingVerification, "Accepted");

        var job = new DeploymentJob
        {
            ProjectId = projectId, TriggerCommitSha = sha, Status = JobStatus.Succeeded,
            FinishedAt = finishedAt, NuGetVerification = NuGetVerificationStatus.Pending
        };
        var storage = Substitute.For<IFleetStorage>();
        storage.GetJobsAsync(Arg.Any<CancellationToken>()).Returns(_ => new List<DeploymentJob> { job });

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IFleetStorage)).Returns(storage);
        scope.ServiceProvider.GetService(typeof(NuGetReleaseStore)).Returns(releases);
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(scope);
        return (job, releases, new NuGetReleaseVerificationService(scopes, NullLogger<NuGetReleaseVerificationService>.Instance));
    }

    private static byte[] MakePackage(string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspec = zip.CreateEntry("DemoLib.nuspec");
            using (var writer = new StreamWriter(nuspec.Open(), Encoding.UTF8))
                writer.Write("<package><metadata><id>DemoLib</id><version>1.0.0</version><authors>Fleet</authors><description>Test</description></metadata></package>");
            var entry = zip.CreateEntry("lib/net10.0/content.txt");
            using var content = new StreamWriter(entry.Open(), Encoding.UTF8);
            content.Write(body);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
