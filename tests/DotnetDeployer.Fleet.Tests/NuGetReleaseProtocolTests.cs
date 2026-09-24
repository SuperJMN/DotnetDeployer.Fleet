using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Execution;
using DotnetDeployer.Fleet.WorkerService.Coordinator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class NuGetReleaseProtocolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fleet-release-protocol-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Manifest_and_package_progress_survive_coordinator_restart_and_bytes_cannot_change()
    {
        var package = MakePackage("DemoLib", "1.0.0", "original");
        var projectId = Guid.NewGuid();
        var sha = new string('a', 40);
        var writer = new NuGetReleaseStore(root);
        await writer.UploadPackageAsync(projectId, sha, "DemoLib", new MemoryStream(package));
        var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var manifest = new NuGetReleaseManifest(projectId, sha, "1.0.0", "staging-feed", "NUGET_API_KEY", false,
            [new NuGetReleasePackage("DemoLib", "1.0.0", "packages/DemoLib.nupkg", hash, "content-hash")],
            DateTimeOffset.UtcNow);
        await writer.CreateAsync(manifest);
        await writer.SetStateAsync(projectId, sha, "DemoLib", NuGetReleasePackageState.Publishing, "push started");

        var restarted = new NuGetReleaseStore(root);
        var snapshot = await restarted.GetAsync(projectId, sha);
        snapshot!.Manifest.Should().BeEquivalentTo(manifest);
        snapshot.Progress.Single().State.Should().Be(NuGetReleasePackageState.Publishing);
        using var stored = restarted.OpenPackage(projectId, sha, "DemoLib");
        var bytes = new byte[package.Length];
        (await stored.ReadAsync(bytes)).Should().Be(package.Length);
        bytes.Should().Equal(package);
        var overwrite = async () => await restarted.UploadPackageAsync(projectId, sha, "DemoLib", new MemoryStream([1, 2]));
        await overwrite.Should().ThrowAsync<InvalidOperationException>();

        await restarted.SetStateAsync(projectId, sha, "DemoLib", NuGetReleasePackageState.Complete, "verified");
        await restarted.SetStateAsync(projectId, sha, "DemoLib", NuGetReleasePackageState.InterventionRequired, "feed changed");
        var unsafeReset = async () => await writer.SetStateAsync(projectId, sha, "DemoLib", NuGetReleasePackageState.Prepared, null);
        await unsafeReset.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task V3_feed_lookup_checks_exact_download_and_detects_equivalent_conflict_and_delay()
    {
        var expectedPath = Path.Combine(root, "DemoLib.1.0.0.nupkg");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(expectedPath, MakePackage("DemoLib", "1.0.0", "expected"));
        var expected = await NuGetFeedVerifier.ReadIdentityAsync(expectedPath, CancellationToken.None);
        var handler = new MutableFeedHandler();
        var verifier = new NuGetFeedVerifier(new HttpClient(handler));
        const string source = "https://staging.invalid/v3/index.json";

        (await verifier.CheckAsync(source, expected, CancellationToken.None)).Should().Be(FeedPackageStatus.Missing);
        handler.Package = await File.ReadAllBytesAsync(expectedPath);
        (await verifier.CheckAsync(source, expected, CancellationToken.None)).Should().Be(FeedPackageStatus.Equivalent);
        handler.Package = MakePackage("DemoLib", "1.0.0", "conflicting");
        (await verifier.CheckAsync(source, expected, CancellationToken.None)).Should().Be(FeedPackageStatus.Conflict);

        handler.Package = null;
        var delayed = Task.Run(async () =>
        {
            await Task.Delay(80);
            handler.Package = await File.ReadAllBytesAsync(expectedPath);
        });
        (await verifier.WaitForExactAsync(source, expected, TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(20), CancellationToken.None)).Should().Be(FeedPackageStatus.Equivalent);
        await delayed;
    }

    [Fact]
    public async Task Remote_worker_staged_download_remains_readable_after_http_response_disposal()
    {
        var expected = MakePackage("DemoLib", "1.0.0", "durable");
        using var http = new HttpClient(new SinglePackageHandler(expected)) { BaseAddress = new Uri("https://coordinator.invalid") };
        var source = new RemoteWorkerJobSource(http, NullLogger<RemoteWorkerJobSource>.Instance);
        await using var download = await source.DownloadNuGetReleasePackageAsync(Guid.NewGuid(), new string('a', 40), "DemoLib");
        using var bytes = new MemoryStream();
        await download.CopyToAsync(bytes);
        bytes.ToArray().Should().Equal(expected);
    }

    private static byte[] MakePackage(string id, string version, string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspec = zip.CreateEntry(id + ".nuspec");
            using (var writer = new StreamWriter(nuspec.Open(), Encoding.UTF8))
                writer.Write($"<package><metadata><id>{id}</id><version>{version}</version><authors>Fleet</authors><description>Test</description></metadata></package>");
            var entry = zip.CreateEntry("lib/net10.0/content.txt");
            using var content = new StreamWriter(entry.Open(), Encoding.UTF8);
            content.Write(body);
        }
        return output.ToArray();
    }

    private sealed class MutableFeedHandler : HttpMessageHandler
    {
        public byte[]? Package { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = request.RequestUri!.AbsolutePath.EndsWith("index.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"resources\":[{\"@id\":\"https://staging.invalid/flat/\",\"@type\":\"PackageBaseAddress/3.0.0\"}]}")
                }
                : Package is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) };
            return Task.FromResult(response);
        }
    }

    private sealed class SinglePackageHandler(byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}
