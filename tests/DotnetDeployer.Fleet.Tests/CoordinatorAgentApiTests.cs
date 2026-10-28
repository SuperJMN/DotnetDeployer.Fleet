using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DotnetDeployer.Fleet.Coordinator.Auth;
using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.WorkerService.Bootstrap;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;

namespace DotnetDeployer.Fleet.Tests;

public sealed class CoordinatorAgentApiTests
{
    private static ClaimsPrincipal User(UserRole role) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "jmn"),
            new Claim(ClaimTypes.Role, role.ToString())
        ], "test"));

    private static JwtService Jwt() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-please-be-at-least-32-bytes-long!!"
        }).Build());

    [Fact]
    public void Log_pages_number_lines_and_apply_since_before_tail()
    {
        var entries = Enumerable.Range(1, 10).Select(i => new LogEntry { Line = $"line {i}" }).ToList();

        var page = JobLogPage.From(Guid.NewGuid(), JobStatus.Running, entries, since: 3, tail: 2);

        page.TotalLines.Should().Be(10);
        page.Lines.Select(l => (l.Number, l.Text)).Should().Equal((9, "line 9"), (10, "line 10"));
    }

    [Fact]
    public void Readers_never_receive_secret_values_or_git_tokens()
    {
        var reader = User(UserRole.Reader);
        var admin = User(UserRole.Admin);

        AccessPolicies.RedactFor(reader, new Secret { Name = "NUGET_API_KEY", Value = "v" }).Value.Should().BeEmpty();
        AccessPolicies.RedactFor(reader, new Project { GitToken = "ghp_x" }).GitToken.Should().BeNull();
        AccessPolicies.RedactFor(admin, new Secret { Value = "v" }).Value.Should().Be("v");
        AccessPolicies.RedactFor(admin, new Project { GitToken = "ghp_x" }).GitToken.Should().Be("ghp_x");
    }

    [Fact]
    public void Api_tokens_are_read_only_by_default_and_never_exceed_the_callers_role()
    {
        var context = new DefaultHttpContext { User = User(UserRole.Operator) };

        var readOnly = (Ok<ApiTokenResponse>)AuthEndpoints.CreateApiToken(new CreateApiTokenRequest(), context, Jwt());
        var write = (Ok<ApiTokenResponse>)AuthEndpoints.CreateApiToken(new CreateApiTokenRequest(ReadOnly: false, Days: 1000), context, Jwt());

        RoleOf(readOnly.Value!.Token).Should().Be("Reader");
        RoleOf(write.Value!.Token).Should().Be("Operator");
        write.Value.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(AuthEndpoints.MaxApiTokenDays), TimeSpan.FromMinutes(1));
    }

    private static string RoleOf(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(c => c.Type == ClaimTypes.Role || c.Type == "role").Value;

    [Fact]
    public async Task Releases_survive_cleared_jobs_and_show_the_publishing_job_when_present()
    {
        var root = Directory.CreateTempSubdirectory("fleet-releases-").FullName;
        try
        {
            var store = new NuGetReleaseStore(root);
            var projectId = Guid.NewGuid();
            var cleared = new string('a', 40);
            var kept = new string('b', 40);
            await Prepare(store, projectId, cleared, "1.0.0", DateTimeOffset.UtcNow.AddDays(-1));
            await Prepare(store, projectId, kept, "1.1.0", DateTimeOffset.UtcNow);
            await store.SetStateAsync(projectId, kept, "Acme.Lib", NuGetReleasePackageState.Complete, "verified");
            var job = new DeploymentJob
            {
                ProjectId = projectId, TriggerCommitSha = kept, Status = JobStatus.Succeeded,
                NuGetVerification = NuGetVerificationStatus.Verified
            };

            var releases = ProjectReleases.Build(await store.ListAsync(projectId), [job]);

            releases.Select(r => r.Version).Should().Equal("1.1.0", "1.0.0");
            releases[0].Should().BeEquivalentTo(new
            {
                JobId = (Guid?)job.Id,
                Verification = (NuGetVerificationStatus?)NuGetVerificationStatus.Verified,
                Packages = new[] { new { Id = "Acme.Lib", State = NuGetReleasePackageState.Complete } }
            });
            releases[1].JobId.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Prepare(NuGetReleaseStore store, Guid projectId, string sha, string version, DateTimeOffset preparedAt)
    {
        byte[] bytes = [1, 2, 3];
        await store.UploadPackageAsync(projectId, sha, "Acme.Lib", new MemoryStream(bytes));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        await store.CreateAsync(new NuGetReleaseManifest(projectId, sha, version, "https://api.nuget.org/v3/index.json", "NUGET_API_KEY", false,
            [new NuGetReleasePackage("Acme.Lib", version, "packages/Acme.Lib.nupkg", hash, "content")], preparedAt));
    }

    [Fact]
    public void Workers_report_the_ids_of_their_installed_workloads()
    {
        const string output = """

            Installed Workload Id      Manifest Version       Installation Source
            ---------------------------------------------------------------------
            android                    35.0.7/9.0.100         SDK 9.0.100
            wasm-tools                 9.0.0/9.0.100          SDK 9.0.100

            Use `dotnet workload search` to find additional workloads to install.
            """;

        WorkerEnvironmentProbe.ParseWorkloadList(output).Should().Equal("android", "wasm-tools");
    }
}
