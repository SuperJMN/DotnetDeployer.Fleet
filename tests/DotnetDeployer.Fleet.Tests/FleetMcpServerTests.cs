using System.Text.Json.Nodes;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Tooling.Agents;
using DotnetDeployer.Fleet.Tooling.Agents.Mcp;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FleetMcpServerTests
{
    private readonly FakeCoordinator coordinator = new();

    private Task<IReadOnlyList<JsonObject>> Exchange(params object[] requests)
    {
        var operations = new FleetOperations(coordinator.CreateClient(), delay: (_, _) => Task.CompletedTask);
        return Exchange(_ => Task.FromResult(operations), requests);
    }

    private static async Task<IReadOnlyList<JsonObject>> Exchange(Func<CancellationToken, Task<FleetOperations>> connect, params object[] requests)
    {
        var server = new FleetMcpServer(new FleetMcpTools(connect));
        var input = new StringReader(string.Join('\n', requests.Select(r => System.Text.Json.JsonSerializer.Serialize(r))) + "\n");
        var output = new StringWriter();

        await server.RunAsync(input, output);

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();
    }

    private static object Call(int id, string tool, object arguments, object? meta = null) => new
    {
        jsonrpc = "2.0",
        id,
        method = "tools/call",
        @params = meta is null ? (object)new { name = tool, arguments } : new { name = tool, arguments, _meta = meta }
    };

    private static JsonObject Response(IEnumerable<JsonObject> messages, int id) =>
        messages.Single(m => m["id"]?.GetValue<int>() == id);

    [Fact]
    public async Task Initialize_negotiates_the_protocol_and_announces_tools_and_resources()
    {
        var messages = await Exchange(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }
        });

        var result = Response(messages, 1)["result"]!;
        result["protocolVersion"]!.GetValue<string>().Should().Be("2025-03-26");
        result["capabilities"]!["tools"].Should().NotBeNull();
        result["capabilities"]!["resources"].Should().NotBeNull();
        result["serverInfo"]!["name"]!.GetValue<string>().Should().Be("fleet");
    }

    [Fact]
    public async Task Tools_are_annotated_so_queries_can_be_allowed_and_destructive_ones_confirmed()
    {
        var messages = await Exchange(new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        var tools = Response(messages, 1)["result"]!["tools"]!.AsArray()
            .ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["annotations"]!);
        tools.Keys.Should().Contain(["list_projects", "get_job", "wait_for_job", "get_job_failure", "tail_logs", "enqueue_deploy"]);
        tools["list_jobs"]["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();
        tools["wait_for_job"]["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();
        tools["enqueue_deploy"]["readOnlyHint"]!.GetValue<bool>().Should().BeFalse();
        tools["set_secret"]["destructiveHint"]!.GetValue<bool>().Should().BeTrue();
        tools["delete_secret"]["destructiveHint"]!.GetValue<bool>().Should().BeTrue();
        tools["cancel_job"]["destructiveHint"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_tool_call_returns_the_same_json_as_the_cli()
    {
        coordinator.AddProject("App");

        var messages = await Exchange(Call(1, "list_projects", new { }));

        var result = Response(messages, 1)["result"]!;
        result["isError"]!.GetValue<bool>().Should().BeFalse();
        var projects = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsArray();
        projects.Should().ContainSingle();
        projects[0]!["name"]!.GetValue<string>().Should().Be("App");
    }

    [Fact]
    public async Task Waiting_for_a_job_reports_progress_and_the_final_exit_code()
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Failed);

        var messages = await Exchange(Call(7, "wait_for_job", new { job = job.Id.ToString(), timeout_seconds = 30 }, new { progressToken = "w1" }));

        messages.Should().Contain(m => m["method"] != null
                                       && m["method"]!.GetValue<string>() == "notifications/progress"
                                       && m["params"]!["progressToken"]!.GetValue<string>() == "w1");
        var result = Response(messages, 7)["result"]!;
        result["isError"]!.GetValue<bool>().Should().BeTrue();
        JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!["exitCode"]!.GetValue<int>().Should().Be(FleetExitCode.JobFailed);
    }

    [Fact]
    public async Task Errors_are_reported_as_tool_errors_not_protocol_failures()
    {
        var messages = await Exchange(
            Call(1, "get_project", new { project = "missing" }),
            Call(2, "no_such_tool", new { }),
            Call(3, "get_job", new { }));

        foreach (var id in new[] { 1, 2, 3 })
            Response(messages, id)["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Job_logs_are_readable_as_a_resource()
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Succeeded, null, "line one", "line two");

        var messages = await Exchange(new
        {
            jsonrpc = "2.0", id = 1, method = "resources/read",
            @params = new { uri = $"fleet://jobs/{job.Id}/logs" }
        });

        Response(messages, 1)["result"]!["contents"]![0]!["text"]!.GetValue<string>()
            .Should().Be("line one\nline two\n");
    }

    [Fact]
    public async Task A_coordinator_that_does_not_answer_in_time_still_gets_a_response()
    {
        Task<FleetOperations> TimesOut(CancellationToken _) => Task.FromException<FleetOperations>(new TaskCanceledException("HttpClient.Timeout elapsed"));

        var messages = await Exchange(TimesOut,
            Call(1, "list_projects", new { }),
            new { jsonrpc = "2.0", id = 2, method = "resources/read", @params = new { uri = $"fleet://jobs/{Guid.NewGuid()}/logs" } });

        var tool = Response(messages, 1)["result"]!;
        tool["isError"]!.GetValue<bool>().Should().BeTrue();
        tool["content"]![0]!["text"]!.GetValue<string>().Should().Be(FleetMcpTools.TimeoutMessage);
        Response(messages, 2)["error"]!["message"]!.GetValue<string>().Should().Be(FleetMcpTools.TimeoutMessage);
    }

    [Fact]
    public async Task Unknown_methods_get_a_json_rpc_error()
    {
        var messages = await Exchange(new { jsonrpc = "2.0", id = 1, method = "sampling/whatever" });

        Response(messages, 1)["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
    }
}
