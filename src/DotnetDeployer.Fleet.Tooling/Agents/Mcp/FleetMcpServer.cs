using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetDeployer.Fleet.Tooling.Agents.Mcp;

/// <summary>
/// Minimal Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0). It serves
/// the tools of <see cref="FleetMcpTools"/> and the <c>fleet://jobs/{id}/logs</c> resource.
/// Requests run concurrently, so a long <c>wait_for_job</c> does not block other calls, and a
/// client can cancel one with <c>notifications/cancelled</c>.
/// </summary>
public sealed class FleetMcpServer(FleetMcpTools tools)
{
    private static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private const string Instructions =
        "Fleet is a self-hosted CI/CD for .NET repositories. Typical flow: list_projects or ensure_project, " +
        "check_project, enqueue_deploy, wait_for_job, and when it failed get_job_failure (phase, build errors, " +
        "failed tests, suggestions) before reading tail_logs. list_workers shows each worker's build environment; " +
        "list_releases shows what Fleet published to NuGet and versions on the feed it did not publish. " +
        "Secret values are never returned.";

    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> inFlight = new();

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var pending = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                await WriteAsync(output, Error(null, -32700, "Parse error"), ct);
                continue;
            }

            if (message is null)
            {
                await WriteAsync(output, Error(null, -32600, "Invalid request"), ct);
                continue;
            }

            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(HandleAsync(message, output, ct));
        }

        await Task.WhenAll(pending);
    }

    private async Task HandleAsync(JsonObject message, TextWriter output, CancellationToken ct)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        var parameters = message["params"] as JsonObject;

        if (id is null)
        {
            if (method == "notifications/cancelled"
                && parameters?["requestId"]?.ToJsonString() is { } requestId
                && inFlight.TryGetValue(requestId, out var cts))
            {
                await cts.CancelAsync();
            }

            return;
        }

        var key = id.ToJsonString();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inFlight[key] = requestCts;
        try
        {
            var response = method switch
            {
                "initialize" => Result(id, Initialize(parameters)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = tools.Describe() }),
                "tools/call" => Result(id, await CallToolAsync(parameters, output, requestCts.Token)),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "resources/templates/list" => Result(id, new JsonObject { ["resourceTemplates"] = FleetMcpTools.ResourceTemplates() }),
                "resources/read" => await ReadResourceAsync(id, parameters, requestCts.Token),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => Error(id, -32601, $"Method not found: {method}")
            };
            await WriteAsync(output, response, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled by the client: MCP says no response is sent for a cancelled request.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteAsync(output, Error(id, -32603, ex.Message), ct);
        }
        finally
        {
            inFlight.TryRemove(key, out _);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        var version = requested is not null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : SupportedProtocolVersions[0];

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "fleet",
                ["title"] = "DotnetDeployer.Fleet",
                ["version"] = (Assembly.GetEntryAssembly() ?? typeof(FleetMcpServer).Assembly).GetName().Version?.ToString(3) ?? "0.0.0"
            },
            ["instructions"] = Instructions
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, TextWriter output, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        var progressToken = parameters?["_meta"]?["progressToken"]?.DeepClone();
        var progressCount = 0;

        void Progress(string text)
        {
            if (progressToken is null) return;
            var notification = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new JsonObject
                {
                    ["progressToken"] = progressToken.DeepClone(),
                    ["progress"] = ++progressCount,
                    ["message"] = text
                }
            };
            _ = WriteAsync(output, notification, CancellationToken.None);
        }

        var outcome = await tools.CallAsync(name, arguments, Progress, ct);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Text }),
            ["isError"] = outcome.IsError
        };
    }

    private async Task<JsonObject> ReadResourceAsync(JsonNode id, JsonObject? parameters, CancellationToken ct)
    {
        var uri = parameters?["uri"]?.GetValue<string>() ?? "";
        var text = await tools.ReadResourceAsync(uri, ct);
        if (text is null)
            return Error(id, -32002, $"Resource not found: {uri}");

        return Result(id, new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["uri"] = uri,
                ["mimeType"] = "text/plain",
                ["text"] = text
            })
        });
    }

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };

    private async Task WriteAsync(TextWriter output, JsonObject message, CancellationToken ct)
    {
        var json = message.ToJsonString();
        await writeGate.WaitAsync(ct);
        try
        {
            await output.WriteLineAsync(json);
            await output.FlushAsync(ct);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
