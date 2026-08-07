using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectTestSettingsApiTests
{
    [Fact]
    public void Create_request_without_setting_enables_tests()
    {
        var request = JsonSerializer.Deserialize<ProjectEndpoints.CreateProjectRequest>("""
            {
              "name": "App",
              "gitUrl": "https://example.com/app.git"
            }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var project = ProjectEndpoints.CreateProject(request!);

        project.RunTestsBeforeDeploy.Should().BeTrue();
    }

    [Fact]
    public void Update_request_can_disable_reenable_and_leave_setting_unchanged()
    {
        var project = new Project { RunTestsBeforeDeploy = true };

        ProjectEndpoints.ApplyUpdate(project, Update(runTestsBeforeDeploy: false));
        project.RunTestsBeforeDeploy.Should().BeFalse();

        ProjectEndpoints.ApplyUpdate(project, Update(runTestsBeforeDeploy: null));
        project.RunTestsBeforeDeploy.Should().BeFalse();

        ProjectEndpoints.ApplyUpdate(project, Update(runTestsBeforeDeploy: true));
        project.RunTestsBeforeDeploy.Should().BeTrue();
    }

    [Fact]
    public async Task Fleet_client_sends_test_setting_on_create_and_update()
    {
        var requests = new List<(HttpMethod Method, string Body)>();
        var handler = new StubHandler(async request =>
        {
            requests.Add((request.Method, await request.Content!.ReadAsStringAsync()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Post
                    ? JsonContent.Create(new Project())
                    : null
            };
        });
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");

        await client.CreateProjectAsync(
            "App",
            "https://example.com/app.git",
            "main",
            runTestsBeforeDeploy: false);
        await client.UpdateProjectAsync(Guid.NewGuid(), runTestsBeforeDeploy: true);

        requests.Should().HaveCount(2);
        requests[0].Method.Should().Be(HttpMethod.Post);
        requests[0].Body.Should().Contain("\"runTestsBeforeDeploy\":false");
        requests[1].Method.Should().Be(HttpMethod.Put);
        requests[1].Body.Should().Contain("\"runTestsBeforeDeploy\":true");
    }

    private static ProjectEndpoints.UpdateProjectRequest Update(bool? runTestsBeforeDeploy) =>
        new(null, null, null, null, RunTestsBeforeDeploy: runTestsBeforeDeploy);

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handle(request);
    }
}
