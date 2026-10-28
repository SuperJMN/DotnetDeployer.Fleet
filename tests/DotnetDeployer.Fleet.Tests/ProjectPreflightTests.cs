using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectPreflightTests : IDisposable
{
    private readonly string repo = Directory.CreateTempSubdirectory("fleet-preflight-").FullName;

    private const string AndroidAndNuGetYaml = """
        version: 1
        github:
          enabled: true
          token:
            from: env
            name: GITHUB_TOKEN
          packages:
            - project: src/App.Android/App.Android.csproj
              formats:
                - type: Apk
                  arch: [arm64]
                  signing:
                    keystore:
                      from: env
                      name: ANDROID_KEYSTORE_BASE64
        nuget:
          enabled: true
          apiKey:
            from: env
            name: NUGET_API_KEY
          packages:
            - project: src/Lib/Lib.csproj
        """;

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static Worker WorkerWith(string name, WorkerEnvironment? environment, WorkerStatus status = WorkerStatus.Online) =>
        new() { Name = name, Status = status, Environment = environment };

    [Fact]
    public void A_ready_project_passes_and_lists_the_workers_that_can_build_it()
    {
        Write("App.slnx", "<Solution />");
        Write("deployer.yaml", AndroidAndNuGetYaml);
        Write("src/Lib/Lib.csproj", "<Project><PropertyGroup><PackageId>Acme.Lib</PackageId></PropertyGroup></Project>");
        var project = new Project { Name = "App", ExpectedPackageIds = ["Acme.Lib"] };
        Worker[] workers =
        [
            WorkerWith("Peach", new WorkerEnvironment { AndroidSdkPath = null, JavaHome = "/jdk" }),
            WorkerWith("rpi4", new WorkerEnvironment { AndroidSdkPath = "/android", JavaHome = "/jdk" })
        ];

        var report = ProjectPreflight.Inspect(repo, project, ["GITHUB_TOKEN", "ANDROID_KEYSTORE_BASE64", "NUGET_API_KEY"], workers);

        report.Passed.Should().BeTrue();
        report.ReferencedSecrets.Should().BeEquivalentTo("ANDROID_KEYSTORE_BASE64", "GITHUB_TOKEN", "NUGET_API_KEY");
        report.PackageIds.Should().Equal("Acme.Lib");
        report.Requirements.Should().Equal("android");
        report.Workers.Should().SatisfyRespectively(
            peach =>
            {
                peach.CanBuild.Should().BeFalse();
                peach.Reasons.Should().ContainSingle().Which.Should().Contain("Android SDK");
            },
            rpi4 => rpi4.CanBuild.Should().BeTrue());
    }

    [Fact]
    public void Missing_secrets_ambiguous_solutions_and_unpinned_packages_are_reported()
    {
        Write("A.sln", "");
        Write("B.sln", "");
        Write("deployer.yaml", AndroidAndNuGetYaml);
        Write("src/Lib/Lib.csproj", "<Project />");
        var project = new Project { Name = "App", ExpectedPackageIds = ["Acme.Lib"] };

        var report = ProjectPreflight.Inspect(repo, project, ["GITHUB_TOKEN"], [WorkerWith("rpi4", null)]);

        report.Passed.Should().BeFalse();
        report.MissingSecrets.Should().BeEquivalentTo("ANDROID_KEYSTORE_BASE64", "NUGET_API_KEY");
        report.Checks.Should().Contain(c => c.Name == "solution" && c.Severity == PreflightSeverity.Error && c.Message.Contains("A.sln, B.sln"));
        report.Checks.Should().Contain(c => c.Name == "packages" && c.Severity == PreflightSeverity.Warning && c.Message.Contains("Lib"));
        report.Checks.Should().Contain(c => c.Name == "workers" && c.Severity == PreflightSeverity.Error);
        report.Workers.Single().Reasons.Should().Contain(r => r.Contains("has not reported"));
    }

    [Fact]
    public void A_repository_without_deployer_yaml_is_not_ready()
    {
        Write("App.sln", "");

        var report = ProjectPreflight.Inspect(repo, new Project { Name = "App" }, [], [WorkerWith("rpi4", null)]);

        report.Checks.Should().Contain(c => c.Name == "deployer.yaml" && c.Severity == PreflightSeverity.Error);
        report.Checks.Should().Contain(c => c.Name == "solution" && c.Severity == PreflightSeverity.Ok);
    }

    [Fact]
    public void Without_a_nuget_package_list_every_packable_project_is_a_candidate()
    {
        Write("App.slnx", "");
        Write("deployer.yaml", "nuget:\n  apiKey:\n    from: env\n    name: NUGET_API_KEY\n");
        Write("src/Tool/Tool.csproj", "<Project><PropertyGroup><PackAsTool>true</PackAsTool><AssemblyName>acme-tool</AssemblyName></PropertyGroup></Project>");
        Write("src/App/App.csproj", "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>");

        var report = ProjectPreflight.Inspect(repo, new Project { Name = "App" }, ["NUGET_API_KEY"], [WorkerWith("rpi4", null)]);

        report.PackageIds.Should().Equal("acme-tool");
        report.Passed.Should().BeTrue();
    }

    public void Dispose() => Directory.Delete(repo, recursive: true);
}
