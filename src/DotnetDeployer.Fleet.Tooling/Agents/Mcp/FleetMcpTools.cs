using System.Text.Json.Nodes;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tooling.Agents.Mcp;

public sealed record McpToolOutcome(string Text, bool IsError);

/// <summary>
/// The MCP tools of <c>fleet mcp</c>: the same operations as the CLI, with MCP annotations so a
/// client can allow the read-only ones permanently and confirm the destructive ones.
/// </summary>
public sealed class FleetMcpTools(Func<CancellationToken, Task<FleetOperations>> connect, Action? onUnauthorized = null)
{
    private const int DefaultWaitSeconds = 600;
    private const int MaxWaitSeconds = 3600;
    private const int DefaultTailLines = 200;

    private delegate Task<object> Handler(JsonObject args, Action<string> progress, FleetOperations ops, CancellationToken ct);

    private sealed record Tool(
        string Name,
        string Description,
        Param[] Parameters,
        bool ReadOnly,
        bool Destructive,
        bool Idempotent,
        bool OpenWorld,
        Handler Handle);

    private sealed record Param(string Name, string Type, string Description, bool Required = false, string[]? Enum = null);

    private static readonly Param Project = new("project", "string", "Project name, id or Git URL", Required: true);
    private static readonly Param Job = new("job", "string", "Job id or a unique id prefix", Required: true);

    private static readonly Tool[] All =
    [
        new("list_projects", "List projects with branch, polling, test gate, pinned packages, last deploy and latest job.",
            [], true, false, true, false,
            async (_, _, ops, ct) => await ops.ListProjectsAsync(ct)),

        new("get_project", "Full configuration of a project and the names (never values) of the secrets it gets.",
            [Project], true, false, true, false,
            async (a, _, ops, ct) => await ops.ShowProjectAsync(Str(a, "project")!, ct)),

        new("check_project", "Check a project against Fleet's requirements without queueing anything: root solution, deployer.yaml, referenced secrets, pinned packages and workers able to build it.",
            [Project], true, false, true, true,
            async (a, _, ops, ct) => await ops.CheckProjectAsync(Str(a, "project")!, ct)),

        new("ensure_project", "Create a project or update only the settings that differ. Idempotent.",
            [
                new("name", "string", "Project name", Required: true),
                new("git_url", "string", "Git repository URL (required to create)"),
                new("branch", "string", "Tracked branch"),
                new("poll_minutes", "integer", "Polling interval in minutes (0 disables)"),
                new("run_tests", "boolean", "Run the solution tests before releasing"),
                new("expected_package_ids", "array", "Package ids a release must publish (empty array removes the pin)")
            ], false, false, true, false,
            async (a, _, ops, ct) => await ops.EnsureProjectAsync(Str(a, "name")!, Str(a, "git_url"), Str(a, "branch"),
                Int(a, "poll_minutes"), Bool(a, "run_tests"), Strings(a, "expected_package_ids"), ct)),

        new("enqueue_deploy", "Queue a deploy of the branch head or a commit. Reports instead of queueing when a deploy of that commit is active or finished, unless force.",
            [Project, new("commit", "string", "Full commit SHA (default: branch head)"), new("force", "boolean", "Queue even when that commit was already deployed")],
            false, false, false, false,
            async (a, _, ops, ct) => await ops.DeployAsync(Str(a, "project")!, Str(a, "commit"), Bool(a, "force") ?? false, ct)),

        new("list_jobs", "List jobs, newest first, with worker, commit, version, current phase, duration and NuGet verification.",
            [
                new("project", "string", "Only this project"),
                new("status", "string", "Only jobs in this status", Enum: Enum.GetNames<JobStatus>()),
                new("limit", "integer", "Maximum number of jobs (default 20)")
            ], true, false, true, false,
            async (a, _, ops, ct) => await ops.ListJobsAsync(Str(a, "project"),
                Str(a, "status") is { } s ? Enum.Parse<JobStatus>(s, ignoreCase: true) : null, Int(a, "limit") ?? 20, ct)),

        new("get_job", "A job's status, phase, worker, commit, version, error and NuGet state per package.",
            [Job], true, false, true, false,
            async (a, _, ops, ct) => await ops.ShowJobAsync(Str(a, "job")!, ct)),

        new("get_job_phases", "A job's phases with status and duration.",
            [Job], true, false, true, false,
            async (a, _, ops, ct) => await ops.GetPhasesAsync(Str(a, "job")!, ct)),

        new("wait_for_job", "Wait until a job finishes (or its NuGet verification completes with until=verified), reporting progress. Returns exitCode 0 succeeded, 1 failed, 2 cancelled, 3 timed out.",
            [
                Job,
                new("until", "string", "finished (default), published or verified", Enum: ["finished", "published", "verified"]),
                new("timeout_seconds", "integer", $"Maximum wait (default {DefaultWaitSeconds}, max {MaxWaitSeconds}); call again to keep waiting")
            ], true, false, true, false,
            async (a, progress, ops, ct) => await ops.WaitAsync(
                Str(a, "job")!,
                Str(a, "until") is { } u ? Enum.Parse<WaitUntil>(u, ignoreCase: true) : WaitUntil.Finished,
                TimeSpan.FromSeconds(Math.Clamp(Int(a, "timeout_seconds") ?? DefaultWaitSeconds, 1, MaxWaitSeconds)),
                job => progress(AgentText.JobLine(job)),
                ct)),

        new("get_job_failure", "Why a job failed: failed phase, MSBuild errors with code and project, failed tests with message and stack, known remedies, and whether it looks like code or infrastructure.",
            [Job], true, false, true, false,
            async (a, _, ops, ct) => await ops.AnalyzeFailureAsync(Str(a, "job")!, ct)),

        new("tail_logs", "A job's log lines; returns right away, also while the job runs. grep (case-insensitive regex) applies before tail.",
            [
                Job,
                new("tail", "integer", $"Last N lines (default {DefaultTailLines})"),
                new("since", "integer", "Only lines after this line number"),
                new("grep", "string", "Only lines matching this regular expression")
            ], true, false, true, false,
            async (a, _, ops, ct) => await ops.GetLogsAsync(Str(a, "job")!, Int(a, "tail") ?? DefaultTailLines, Int(a, "since"), Str(a, "grep"), ct)),

        new("list_job_artifacts", "Artifacts a package build produced.",
            [Job], true, false, true, false,
            async (a, _, ops, ct) => await ops.ListArtifactsAsync(Str(a, "job")!, ct)),

        new("cancel_job", "Cancel a queued or running job.",
            [Job], false, true, true, false,
            async (a, _, ops, ct) => await ops.CancelJobAsync(Str(a, "job")!, ct)),

        new("list_releases", "NuGet releases Fleet published for a project with per-package state and verification, and versions on the feed Fleet did not publish.",
            [Project, new("compare_feed", "boolean", "Compare with the NuGet feed (default true)")], true, false, true, true,
            async (a, _, ops, ct) => await ops.ListReleasesAsync(Str(a, "project")!, Bool(a, "compare_feed") ?? true, ct)),

        new("list_workers", "Workers: status, last heartbeat, version, platform, current job and build environment (Android SDK, JDK, workloads, display, free disk).",
            [], true, false, true, false,
            async (_, _, ops, ct) => await ops.ListWorkersAsync(ct)),

        new("list_secrets", "Secret names and scope (global, plus the project's when given). Values are never returned.",
            [new("project", "string", "Also list this project's secrets")], true, false, true, false,
            async (a, _, ops, ct) => await ops.ListSecretsAsync(Str(a, "project"), ct)),

        new("set_secret", "Create or replace a secret with the value of an environment variable of the MCP server process. The value is never passed as an argument.",
            [
                new("name", "string", "Secret name", Required: true),
                new("from_env", "string", "Environment variable that holds the value", Required: true),
                new("project", "string", "Scope to this project (default: global)"),
                new("overwrite", "boolean", "Replace the value if the secret exists")
            ], false, true, true, false,
            async (a, _, ops, ct) => await ops.SetSecretAsync(Str(a, "name")!, Str(a, "project"),
                AgentCli.ReadSecretValue(Str(a, "from_env"), false, TextReader.Null), Bool(a, "overwrite") ?? false, ct)),

        new("delete_secret", "Delete a secret.",
            [new("name", "string", "Secret name", Required: true), new("project", "string", "The project's secret (default: global)")],
            false, true, true, false,
            async (a, _, ops, ct) => await ops.DeleteSecretAsync(Str(a, "name")!, Str(a, "project"), ct))
    ];

    public static IReadOnlyList<string> ToolNames => All.Select(t => t.Name).ToList();

    public JsonArray Describe() => new(All.Select(DescribeTool).ToArray<JsonNode?>());

    private static JsonObject DescribeTool(Tool tool)
    {
        var properties = new JsonObject();
        foreach (var p in tool.Parameters)
        {
            var schema = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Type == "array") schema["items"] = new JsonObject { ["type"] = "string" };
            if (p.Enum is not null) schema["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
            properties[p.Name] = schema;
        }

        return new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(tool.Parameters.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray())
            },
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = tool.ReadOnly,
                ["destructiveHint"] = tool.Destructive,
                ["idempotentHint"] = tool.Idempotent,
                ["openWorldHint"] = tool.OpenWorld
            }
        };
    }

    public static JsonArray ResourceTemplates() => new(new JsonObject
    {
        ["uriTemplate"] = "fleet://jobs/{id}/logs",
        ["name"] = "job-logs",
        ["description"] = "Full stored log of a job",
        ["mimeType"] = "text/plain"
    });

    public async Task<McpToolOutcome> CallAsync(string name, JsonObject arguments, Action<string> progress, CancellationToken ct)
    {
        var tool = All.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return new McpToolOutcome($"Unknown tool '{name}'. Available: {string.Join(", ", ToolNames)}.", true);

        var missing = tool.Parameters.Where(p => p.Required && string.IsNullOrWhiteSpace(arguments[p.Name]?.ToString())).Select(p => p.Name).ToList();
        if (missing.Count > 0)
            return new McpToolOutcome($"Missing required argument(s): {string.Join(", ", missing)}.", true);

        try
        {
            var ops = await connect(ct);
            var result = await tool.Handle(arguments, progress, ops, ct);
            return new McpToolOutcome(AgentOutput.Serialize(result), IsError: result is WaitResult { ExitCode: not FleetExitCode.Success });
        }
        catch (FleetUsageException ex)
        {
            return new McpToolOutcome(ex.Message, true);
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                onUnauthorized?.Invoke(); // The next call signs in again.
            return new McpToolOutcome(AgentCli.Describe(ex), true);
        }
        catch (ArgumentException ex)
        {
            return new McpToolOutcome($"Invalid argument: {ex.Message}", true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !ct.IsCancellationRequested)
        {
            // HttpClient reports its timeout as a TaskCanceledException; the request itself was not cancelled.
            return new McpToolOutcome(TimeoutMessage, true);
        }
    }

    public const string TimeoutMessage = "The coordinator did not answer in time.";

    /// <summary>Reads <c>fleet://jobs/{id}/logs</c>; null when the URI is not a Fleet resource.</summary>
    public async Task<string?> ReadResourceAsync(string uri, CancellationToken ct)
    {
        const string prefix = "fleet://jobs/";
        const string suffix = "/logs";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal) || !uri.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var job = uri[prefix.Length..^suffix.Length];
        var ops = await connect(ct);
        var logs = await ops.GetLogsAsync(job, null, null, null, ct);
        return AgentText.Logs(logs);
    }

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue value
            ? value.TryGetValue<int>(out var number) ? number
            : value.TryGetValue<double>(out var real) ? (int)real
            : value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed
            : null
            : null;

    private static bool? Bool(JsonObject args, string name) =>
        args[name] is JsonValue value
            ? value.TryGetValue<bool>(out var flag) ? flag
            : value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) ? parsed
            : null
            : null;

    private static IReadOnlyList<string>? Strings(JsonObject args, string name) =>
        args[name] is JsonArray array
            ? array.Select(n => n?.ToString()).OfType<string>().ToList()
            : null;
}
