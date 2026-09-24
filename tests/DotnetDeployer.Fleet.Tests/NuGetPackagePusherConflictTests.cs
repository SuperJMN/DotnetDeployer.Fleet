using System.Diagnostics;
using DotnetDeployer.Fleet.WorkerService.Execution;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class NuGetPackagePusherConflictTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    public void Dispose()
    {
        foreach (var dir in tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }

    [Fact]
    public async Task Push_detects_conflict_when_feed_contains_different_bytes_and_fails_closed()
    {
        var workDir = CreateTempDir("work");
        var feedDir = CreateTempDir("staging-feed");

        var pkgName = "MyPackage.1.0.0.nupkg";
        var localPkg = Path.Combine(workDir, pkgName);
        var existingFeedPkg = Path.Combine(feedDir, pkgName);

        // Different byte contents
        await File.WriteAllBytesAsync(localPkg, [1, 2, 3, 4, 5]);
        await File.WriteAllBytesAsync(existingFeedPkg, [9, 8, 7, 6, 5]);

        var lines = new List<string>();
        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            localPkg,
            "test-key",
            feedDir,
            line => { lines.Add(line); return Task.CompletedTask; });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Conflict");
        result.Error.Should().Contain("hash mismatch");

        // The existing package in feed must NOT have been overwritten
        var feedBytes = await File.ReadAllBytesAsync(existingFeedPkg);
        feedBytes.Should().Equal([9, 8, 7, 6, 5], "feed content must be preserved without corruption");
    }

    [Fact]
    public async Task Push_detects_conflict_in_hierarchical_feed_when_bytes_differ()
    {
        var workDir = CreateTempDir("work");
        var feedDir = CreateTempDir("hierarchical-feed");

        var pkgName = "MyPackage.1.0.0.nupkg";
        var localPkg = Path.Combine(workDir, pkgName);
        var subDir = Path.Combine(feedDir, "mypackage", "1.0.0");
        Directory.CreateDirectory(subDir);
        var existingFeedPkg = Path.Combine(subDir, pkgName);

        await File.WriteAllBytesAsync(localPkg, [10, 20, 30]);
        await File.WriteAllBytesAsync(existingFeedPkg, [40, 50, 60]);

        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            localPkg,
            "test-key",
            feedDir,
            _ => Task.CompletedTask);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Conflict");
        result.Error.Should().Contain("hash mismatch");
    }

    [Fact]
    public async Task Push_succeeds_as_idempotent_release_when_feed_contains_identical_bytes()
    {
        var workDir = CreateTempDir("work");
        var feedDir = CreateTempDir("staging-feed");

        var pkgName = "MyPackage.1.0.0.nupkg";
        var localPkg = Path.Combine(workDir, pkgName);
        var existingFeedPkg = Path.Combine(feedDir, pkgName);

        // Identical byte contents
        byte[] payload = [11, 22, 33, 44, 55];
        await File.WriteAllBytesAsync(localPkg, payload);
        await File.WriteAllBytesAsync(existingFeedPkg, payload);

        var lines = new List<string>();
        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            localPkg,
            "test-key",
            feedDir,
            line => { lines.Add(line); return Task.CompletedTask; });

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        lines.Should().Contain(l => l.Contains("Idempotent release verified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_to_remote_feed_omits_skip_duplicate_flag()
    {
        var workDir = CreateTempDir("work");
        var pkgPath = Path.Combine(workDir, "MyPackage.1.0.0.nupkg");
        await File.WriteAllBytesAsync(pkgPath, [1, 2, 3]);

        ProcessStartInfo? capturedPsi = null;
        var fakeRunner = new FakeStreamingProcessRunner((psi, _) =>
        {
            capturedPsi = psi;
            return Task.FromResult(0);
        });

        // Remote HTTPS URL
        await NuGetPackagePusher.PushAsync(
            workDir,
            pkgPath,
            "test-key",
            "https://api.nuget.org/v3/index.json",
            _ => Task.CompletedTask,
            fakeRunner);

        capturedPsi.Should().NotBeNull();
        capturedPsi!.ArgumentList.Should().NotContain("--skip-duplicate",
            "remote feeds must not pass --skip-duplicate to allow HTTP 409 conflicts to surface");
    }

    [Fact]
    public async Task Push_to_remote_feed_fails_closed_when_feed_returns_409_conflict()
    {
        var workDir = CreateTempDir("work");
        var pkgPath = Path.Combine(workDir, "MyPackage.1.0.0.nupkg");
        await File.WriteAllBytesAsync(pkgPath, [1, 2, 3]);

        var fakeRunner = new FakeStreamingProcessRunner(async (psi, onLine) =>
        {
            await onLine("info : Pushing MyPackage.1.0.0.nupkg to 'https://nuget.example.com/v3/index.json'...");
            await onLine("info :   PUT https://nuget.example.com/v3/package/");
            await onLine("info :   Conflict https://nuget.example.com/v3/package/ 120ms");
            await onLine("error: Response status code does not indicate success: 409 (Conflict).");
            return 1;
        });

        var lines = new List<string>();
        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            pkgPath,
            "test-key",
            "https://nuget.example.com/v3/index.json",
            line => { lines.Add(line); return Task.CompletedTask; },
            fakeRunner);

        result.Success.Should().BeFalse("remote 409 conflict must fail closed");
        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("HTTP 409");
    }

    [Fact]
    public async Task Push_to_remote_feed_fails_closed_if_duplicate_detected_even_on_zero_exit()
    {
        var workDir = CreateTempDir("work");
        var pkgPath = Path.Combine(workDir, "MyPackage.1.0.0.nupkg");
        await File.WriteAllBytesAsync(pkgPath, [1, 2, 3]);

        var fakeRunner = new FakeStreamingProcessRunner(async (psi, onLine) =>
        {
            await onLine("Package 'MyPackage.1.0.0.nupkg' already exists at feed 'https://nuget.example.com/v3/index.json'.");
            return 0; // Simulated process that exited 0 despite duplicate
        });

        var lines = new List<string>();
        var result = await NuGetPackagePusher.PushAsync(
            workDir,
            pkgPath,
            "test-key",
            "https://nuget.example.com/v3/index.json",
            line => { lines.Add(line); return Task.CompletedTask; },
            fakeRunner);

        result.Success.Should().BeFalse("duplicate detected on remote feed must never declare success without artifact verification");
        result.Error.Should().Contain("identity has not been verified");
    }

    [Fact]
    public async Task Push_to_real_http_remote_feed_fails_closed_on_409_conflict()
    {
        var workDir = CreateTempDir("work");
        var pkgPath = Path.Combine(workDir, "MyRealHttpPkg.1.0.0.nupkg");
        await File.WriteAllBytesAsync(pkgPath, [1, 2, 3, 4, 5]);

        var listener = new System.Net.HttpListener();
        int port = GetAvailableTcpPort();
        var baseUri = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(baseUri);
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var listenerTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(cts.Token);
                    var req = context.Request;
                    var res = context.Response;

                    if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/index.json")
                    {
                        var json = $$"""
                        {
                            "version": "3.0.0",
                            "resources": [
                                {
                                    "@id": "{{baseUri}}push",
                                    "@type": "PackagePublish/2.0.0"
                                }
                            ]
                        }
                        """;
                        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                        res.ContentType = "application/json";
                        res.ContentLength64 = bytes.Length;
                        await res.OutputStream.WriteAsync(bytes, cts.Token);
                        res.Close();
                    }
                    else if (req.HttpMethod == "PUT")
                    {
                        // Simulate remote HTTP 409 Conflict
                        res.StatusCode = 409;
                        res.StatusDescription = "Conflict";
                        var msg = System.Text.Encoding.UTF8.GetBytes("Package already exists on server");
                        res.ContentType = "text/plain";
                        res.ContentLength64 = msg.Length;
                        await res.OutputStream.WriteAsync(msg, cts.Token);
                        res.Close();
                        break;
                    }
                    else
                    {
                        res.StatusCode = 404;
                        res.Close();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (System.Net.HttpListenerException) { }
        }, cts.Token);

        try
        {
            var lines = new List<string>();
            var result = await NuGetPackagePusher.PushAsync(
                workDir,
                pkgPath,
                "dummy-api-key",
                $"{baseUri}index.json",
                line => { lines.Add(line); return Task.CompletedTask; },
                cts.Token);

            result.Success.Should().BeFalse("remote feed returned 409 Conflict without --skip-duplicate");
            result.Error.Should().NotBeNull();
            result.Error.Should().Match(e => e.Contains("409") || e.Contains("Conflict"));
        }
        finally
        {
            cts.Cancel();
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { await listenerTask; } catch { }
        }
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeStreamingProcessRunner(
        Func<System.Diagnostics.ProcessStartInfo, Func<string, Task>, Task<int>> handler) : IStreamingProcessRunner
    {
        public Task<int> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, Func<string, Task> onLine, CancellationToken ct = default)
            => handler(startInfo, onLine);
    }
}
