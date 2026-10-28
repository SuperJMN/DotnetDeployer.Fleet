using System.CommandLine;
using System.Globalization;
using System.Net;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Tooling.Agents.Mcp;

namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>
/// Client commands of the <c>fleet</c> tool for people and AI agents: projects, deploys, jobs,
/// logs, diagnosis, releases, workers and secrets, plus <c>fleet mcp</c> exposing the same
/// operations over the Model Context Protocol.
/// </summary>
public static class AgentCli
{
    private static readonly Option<string?> UrlOption = new("--url")
    {
        Description = $"Coordinator URL (default: ${FleetCredentialResolver.UrlVariable} or the desktop client's settings)"
    };

    private static readonly Option<string?> TokenOption = new("--token")
    {
        Description = $"API token (default: ${FleetCredentialResolver.TokenVariable} or the desktop client's settings)"
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the result as JSON with a stable schema"
    };

    /// <summary>Commands whose usage errors map to <see cref="FleetExitCode.UsageOrAuth"/>.</summary>
    internal static readonly HashSet<Command> ClientCommands = [];

    public static void AddTo(RootCommand root)
    {
        root.Subcommands.Add(Login());
        root.Subcommands.Add(Status());
        root.Subcommands.Add(Tokens());
        root.Subcommands.Add(Projects());
        root.Subcommands.Add(Deploy());
        root.Subcommands.Add(Jobs());
        root.Subcommands.Add(Logs());
        root.Subcommands.Add(Releases());
        root.Subcommands.Add(Workers());
        root.Subcommands.Add(Secrets());
        root.Subcommands.Add(McpCommand());
    }

    // ── Session ──────────────────────────────────────────────────────────────

    private static Command Login()
    {
        var command = Client(new Command("login", "Sign in to a coordinator and remember the session (shared with the desktop client)"));
        var userOption = new Option<string?>("--user", "-u") { Description = "Username (prompted when omitted)" };
        var passwordStdinOption = new Option<bool>("--password-stdin") { Description = "Read the password from standard input" };
        command.Options.Add(userOption);
        command.Options.Add(passwordStdinOption);

        command.SetAction((parse, ct) => Execute(parse, async (output, _) =>
        {
            var settings = new FleetSettingsFile(FleetSettingsFile.DefaultPath);
            var url = parse.GetValue(UrlOption)
                      ?? Environment.GetEnvironmentVariable(FleetCredentialResolver.UrlVariable)
                      ?? settings.Load().GetValueOrDefault("endpoint")
                      ?? throw new FleetUsageException("Pass --url with the coordinator address.");

            var user = parse.GetValue(userOption) ?? Prompt("Username: ", secret: false);
            var password = parse.GetValue(passwordStdinOption)
                ? Console.In.ReadLine()
                : Prompt("Password: ", secret: true);
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
                throw new FleetUsageException("Username and password are required.");

            var handler = new HttpClientHandler();
            var client = new FleetApiClient(handler, handler);
            client.SetBaseAddress(url);
            var login = await FleetSession.SignInAsync(client, user, password);
            settings.SaveSession(url.TrimEnd('/'), login.Token, login.Username);

            var result = new LoginResult(url.TrimEnd('/'), login.Username, login.Role, settings.Path);
            output.Write(result, r => $"Signed in to {r.Url} as {r.Username} ({r.Role}). Session saved in {r.SettingsFile}.\n");
            return FleetExitCode.Success;
        }, ct, needsConnection: false));
        return command;
    }

    private static Command Status()
    {
        var command = Client(new Command("status", "Show whether the coordinator is reachable, its version and the signed-in user"));
        command.SetAction((parse, ct) => Execute(parse, async (output, _) =>
        {
            var credentials = ResolveCredentials(parse);
            var status = await StatusAsync(credentials, ct);
            output.Write(status, AgentText.Status);
            return status.Reachable && status.Username is not null ? FleetExitCode.Success : FleetExitCode.UsageOrAuth;
        }, ct, needsConnection: false));
        return command;
    }

    internal static async Task<StatusView> StatusAsync(FleetCredentials credentials, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(credentials.Url))
            return new StatusView(null, false, null, null, null, credentials.Source, "No coordinator configured. Run 'fleet login --url <coordinator>'.");

        var httpHandler = handler ?? new HttpClientHandler();
        var probe = new FleetApiClient(httpHandler, httpHandler);
        probe.SetBaseAddress(credentials.Url);
        FleetApiClient.HealthResponse? health;
        try
        {
            health = await probe.GetHealthAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new StatusView(credentials.Url, false, null, null, null, credentials.Source, ex.Message);
        }

        try
        {
            var client = await new FleetSession(credentials, new FleetSettingsFile(FleetSettingsFile.DefaultPath), handler).ConnectAsync(ct);
            var session = await client.GetSession();
            return new StatusView(credentials.Url, true, health?.Version, session?.Username, session?.Role, credentials.Source, null);
        }
        catch (FleetUsageException ex)
        {
            return new StatusView(credentials.Url, true, health?.Version, null, null, credentials.Source, ex.Message);
        }
    }

    private static Command Tokens()
    {
        var tokens = Client(new Command("tokens", "API tokens for agents and scripts"));
        var create = Client(new Command("create", "Issue an API token (read-only unless --write) and print it"));
        var writeOption = new Option<bool>("--write") { Description = "Give the token the same access as your account instead of read-only" };
        var daysOption = new Option<int?>("--days") { Description = "Validity in days (default 90, max 365)" };
        create.Options.Add(writeOption);
        create.Options.Add(daysOption);
        create.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            var client = await ConnectAsync(parse, ct);
            var token = await client.CreateApiTokenAsync(!parse.GetValue(writeOption), parse.GetValue(daysOption), ct);
            output.Write(token, t => $"{t.Token}\n");
            if (!output.Json)
                output.Progress($"{t(token.Role)} token for {token.Username}, valid until {AgentText.Time(token.ExpiresAt)}. Give it to the agent as {FleetCredentialResolver.TokenVariable}.");
            return FleetExitCode.Success;

            static string t(string role) => role == nameof(UserRole.Reader) ? "Read-only" : role;
        }, ct, needsConnection: false));
        tokens.Subcommands.Add(create);
        return tokens;
    }

    // ── Projects ─────────────────────────────────────────────────────────────

    private static Command Projects()
    {
        var projects = Client(new Command("projects", "List, inspect, register and check projects"));

        var list = Client(new Command("list", "List projects with their settings and latest job"));
        list.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ListProjectsAsync(ct), AgentText.Projects);
            return FleetExitCode.Success;
        }, ct));

        var projectArgument = ProjectArgument();
        var show = Client(new Command("show", "Show a project's full configuration and the names of the secrets it gets"));
        show.Arguments.Add(projectArgument);
        show.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ShowProjectAsync(parse.GetValue(projectArgument)!, ct), AgentText.ProjectDetail);
            return FleetExitCode.Success;
        }, ct));

        var check = Client(new Command("check", "Check a project against Fleet's requirements without queueing anything (exit 1 when not ready)"));
        var checkArgument = ProjectArgument();
        check.Arguments.Add(checkArgument);
        check.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            var report = await ops.CheckProjectAsync(parse.GetValue(checkArgument)!, ct);
            output.Write(report, AgentText.Preflight);
            return report.Passed ? FleetExitCode.Success : FleetExitCode.JobFailed;
        }, ct));

        var ensure = Client(new Command("ensure", "Create the project or update the settings that differ (idempotent)"));
        var nameOption = new Option<string>("--name") { Description = "Project name", Required = true };
        var gitUrlOption = new Option<string?>("--git-url") { Description = "Git repository URL (required to create)" };
        var branchOption = new Option<string?>("--branch") { Description = "Tracked branch (default master when creating)" };
        var pollOption = new Option<int?>("--poll") { Description = "Polling interval in minutes (0 disables polling)" };
        var testsOption = new Option<bool>("--tests") { Description = "Run the solution tests before releasing" };
        var noTestsOption = new Option<bool>("--no-tests") { Description = "Do not run the solution tests before releasing" };
        var expectOption = new Option<string[]>("--expect-package")
        {
            Description = "Pin a package id the release must publish (repeatable)",
            AllowMultipleArgumentsPerToken = false
        };
        var noExpectOption = new Option<bool>("--no-expect-packages") { Description = "Remove the package pin" };
        foreach (var option in new Option[] { nameOption, gitUrlOption, branchOption, pollOption, testsOption, noTestsOption, expectOption, noExpectOption })
            ensure.Options.Add(option);
        ensure.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            if (parse.GetValue(testsOption) && parse.GetValue(noTestsOption))
                throw new FleetUsageException("Use either --tests or --no-tests, not both.");
            bool? tests = parse.GetValue(testsOption) ? true : parse.GetValue(noTestsOption) ? false : null;
            var expected = parse.GetValue(expectOption);
            IReadOnlyList<string>? packages = parse.GetValue(noExpectOption) ? [] : expected is { Length: > 0 } ? expected : null;

            var result = await ops.EnsureProjectAsync(parse.GetValue(nameOption)!, parse.GetValue(gitUrlOption),
                parse.GetValue(branchOption), parse.GetValue(pollOption), tests, packages, ct);
            output.Write(result, AgentText.Ensure);
            return FleetExitCode.Success;
        }, ct));

        projects.Subcommands.Add(list);
        projects.Subcommands.Add(show);
        projects.Subcommands.Add(ensure);
        projects.Subcommands.Add(check);
        return projects;
    }

    // ── Deploy ───────────────────────────────────────────────────────────────

    private static Command Deploy()
    {
        var command = Client(new Command("deploy", "Queue a deploy of a project (the branch head unless --commit). Writes."));
        var projectArgument = ProjectArgument();
        var commitOption = new Option<string?>("--commit") { Description = "Full commit SHA to deploy" };
        var forceOption = new Option<bool>("--force") { Description = "Queue even if a deploy of that commit is active or already finished" };
        command.Arguments.Add(projectArgument);
        command.Options.Add(commitOption);
        command.Options.Add(forceOption);
        command.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            var result = await ops.DeployAsync(parse.GetValue(projectArgument)!, parse.GetValue(commitOption), parse.GetValue(forceOption), ct);
            output.Write(result, AgentText.Deploy);
            return FleetExitCode.Success;
        }, ct));
        return command;
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────

    private static Command Jobs()
    {
        var jobs = Client(new Command("jobs", "Inspect, wait for, diagnose and cancel jobs"));

        var list = Client(new Command("list", "List jobs, newest first"));
        var projectOption = new Option<string?>("--project") { Description = "Only this project (name, id or Git URL)" };
        var statusOption = new Option<JobStatus?>("--status") { Description = "Only jobs in this status" };
        var limitOption = new Option<int>("--limit") { Description = "Maximum number of jobs", DefaultValueFactory = _ => 20 };
        list.Options.Add(projectOption);
        list.Options.Add(statusOption);
        list.Options.Add(limitOption);
        list.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ListJobsAsync(parse.GetValue(projectOption), parse.GetValue(statusOption), parse.GetValue(limitOption), ct), AgentText.Jobs);
            return FleetExitCode.Success;
        }, ct));

        var showArgument = JobArgument();
        var show = Client(new Command("show", "Show a job: status, phase, worker, commit, version, error and NuGet state per package"));
        show.Arguments.Add(showArgument);
        show.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ShowJobAsync(parse.GetValue(showArgument)!, ct), AgentText.Job);
            return FleetExitCode.Success;
        }, ct));

        var waitArgument = JobArgument();
        var wait = Client(new Command("wait", "Wait for a job. Exit 0 succeeded, 1 failed, 2 cancelled, 3 timed out"));
        var untilOption = new Option<WaitUntil>("--until")
        {
            Description = "finished, published, or verified (also waits for the NuGet verification)",
            DefaultValueFactory = _ => WaitUntil.Finished
        };
        var timeoutOption = new Option<string?>("--timeout") { Description = "Maximum wait, e.g. 90s, 30m, 2h (default: no limit)" };
        wait.Arguments.Add(waitArgument);
        wait.Options.Add(untilOption);
        wait.Options.Add(timeoutOption);
        wait.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            var timeout = ParseDuration(parse.GetValue(timeoutOption));
            var result = await ops.WaitAsync(parse.GetValue(waitArgument)!, parse.GetValue(untilOption), timeout,
                job => output.Progress(AgentText.JobLine(job)), ct);
            output.Write(result, AgentText.Wait);
            return result.ExitCode;
        }, ct));

        var phasesArgument = JobArgument();
        var phases = Client(new Command("phases", "Show a job's phases with their duration"));
        phases.Arguments.Add(phasesArgument);
        phases.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.GetPhasesAsync(parse.GetValue(phasesArgument)!, ct), AgentText.Phases);
            return FleetExitCode.Success;
        }, ct));

        var failureArgument = JobArgument();
        var failure = Client(new Command("failure", "Summarize why a job failed: phase, build errors, failed tests and suggestions"));
        failure.Arguments.Add(failureArgument);
        failure.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.AnalyzeFailureAsync(parse.GetValue(failureArgument)!, ct), AgentText.Failure);
            return FleetExitCode.Success;
        }, ct));

        var cancelArgument = JobArgument();
        var cancel = Client(new Command("cancel", "Cancel a job. Requires --yes"));
        var yesOption = YesOption();
        cancel.Arguments.Add(cancelArgument);
        cancel.Options.Add(yesOption);
        cancel.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            RequireYes(parse, yesOption, "Cancelling a job");
            var job = await ops.CancelJobAsync(parse.GetValue(cancelArgument)!, ct);
            output.Write(job, j => $"Cancellation requested for {j.Id} ({j.Status}).\n");
            return FleetExitCode.Success;
        }, ct));

        var artifactsArgument = JobArgument();
        var artifacts = Client(new Command("artifacts", "List the artifacts a package build produced"));
        artifacts.Arguments.Add(artifactsArgument);
        artifacts.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ListArtifactsAsync(parse.GetValue(artifactsArgument)!, ct), AgentText.Artifacts);
            return FleetExitCode.Success;
        }, ct));

        var downloadJob = JobArgument();
        var downloadPath = new Argument<string>("path") { Description = "Artifact path as listed by 'fleet jobs artifacts'" };
        var outputOption = new Option<string?>("--output", "-o") { Description = "Destination file or directory (default: current directory)" };
        var download = Client(new Command("download", "Download one artifact of a job"));
        download.Arguments.Add(downloadJob);
        download.Arguments.Add(downloadPath);
        download.Options.Add(outputOption);
        download.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            var result = await ops.DownloadArtifactAsync(parse.GetValue(downloadJob)!, parse.GetValue(downloadPath)!, parse.GetValue(outputOption), ct);
            output.Write(result, r => $"Saved {r.OutputPath} ({AgentText.Size(r.SizeBytes)}).\n");
            return FleetExitCode.Success;
        }, ct));

        foreach (var sub in new[] { list, show, wait, phases, failure, cancel, artifacts, download })
            jobs.Subcommands.Add(sub);
        return jobs;
    }

    private static Command Logs()
    {
        var command = Client(new Command("logs", "Print a job's log. Ends right away unless --follow, also for running jobs"));
        var jobArgument = JobArgument();
        var tailOption = new Option<int?>("--tail") { Description = "Only the last N lines (after --grep)" };
        var sinceOption = new Option<int?>("--since") { Description = "Only lines after this line number" };
        var grepOption = new Option<string?>("--grep") { Description = "Only lines matching this case-insensitive regular expression" };
        var followOption = new Option<bool>("--follow", "-f") { Description = "Keep printing new lines until the job finishes" };
        command.Arguments.Add(jobArgument);
        command.Options.Add(tailOption);
        command.Options.Add(sinceOption);
        command.Options.Add(grepOption);
        command.Options.Add(followOption);
        command.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            var job = parse.GetValue(jobArgument)!;
            var (tail, since, grep) = (parse.GetValue(tailOption), parse.GetValue(sinceOption), parse.GetValue(grepOption));
            if (parse.GetValue(followOption))
            {
                await ops.FollowLogsAsync(job, tail, since, grep,
                    line => output.Out.WriteLine(output.Json ? AgentOutput.Serialize(line).ReplaceLineEndings(" ") : line.Text), ct);
                return FleetExitCode.Success;
            }

            output.Write(await ops.GetLogsAsync(job, tail, since, grep, ct), AgentText.Logs);
            return FleetExitCode.Success;
        }, ct));
        return command;
    }

    // ── Releases, workers, secrets ──────────────────────────────────────────

    private static Command Releases()
    {
        var command = Client(new Command("releases", "NuGet versions Fleet published for a project, and versions on the feed it did not publish"));
        var projectArgument = ProjectArgument();
        var noFeedOption = new Option<bool>("--no-feed") { Description = "Do not compare with the NuGet feed" };
        command.Arguments.Add(projectArgument);
        command.Options.Add(noFeedOption);
        command.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ListReleasesAsync(parse.GetValue(projectArgument)!, !parse.GetValue(noFeedOption), ct), AgentText.Releases);
            return FleetExitCode.Success;
        }, ct));
        return command;
    }

    private static Command Workers()
    {
        var workers = Client(new Command("workers", "Workers registered with the coordinator"));
        var list = Client(new Command("list", "List workers: status, last heartbeat, version, platform, current job and build environment"));
        list.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ListWorkersAsync(ct), AgentText.Workers);
            return FleetExitCode.Success;
        }, ct));
        workers.Subcommands.Add(list);
        return workers;
    }

    private static Command Secrets()
    {
        var secrets = Client(new Command("secrets", "Secret names and scope. Values are never printed"));

        var list = Client(new Command("list", "List secret names: global ones, plus the project's with --project"));
        var listProject = new Option<string?>("--project") { Description = "Also list this project's secrets" };
        list.Options.Add(listProject);
        list.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            output.Write(await ops.ListSecretsAsync(parse.GetValue(listProject), ct), AgentText.Secrets);
            return FleetExitCode.Success;
        }, ct));

        var set = Client(new Command("set", "Create or replace a secret from an environment variable or stdin, never from an argument"));
        var setName = new Argument<string>("name") { Description = "Secret (environment variable) name" };
        var setProject = new Option<string?>("--project") { Description = "Scope the secret to this project (default: global)" };
        var fromEnvOption = new Option<string?>("--from-env") { Description = "Read the value from this environment variable" };
        var stdinOption = new Option<bool>("--stdin") { Description = "Read the value from standard input" };
        var setYes = YesOption("Replace the value when the secret already exists");
        set.Arguments.Add(setName);
        foreach (var option in new Option[] { setProject, fromEnvOption, stdinOption, setYes })
            set.Options.Add(option);
        set.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            var value = ReadSecretValue(parse.GetValue(fromEnvOption), parse.GetValue(stdinOption), Console.In);
            var result = await ops.SetSecretAsync(parse.GetValue(setName)!, parse.GetValue(setProject), value, parse.GetValue(setYes), ct);
            output.Write(result, r => $"{r.Outcome} secret {r.Secret.Name} [{r.Secret.Scope}].\n");
            return FleetExitCode.Success;
        }, ct));

        var delete = Client(new Command("delete", "Delete a secret. Requires --yes"));
        var deleteName = new Argument<string>("name") { Description = "Secret name" };
        var deleteProject = new Option<string?>("--project") { Description = "Delete the project's secret (default: global)" };
        var deleteYes = YesOption();
        delete.Arguments.Add(deleteName);
        delete.Options.Add(deleteProject);
        delete.Options.Add(deleteYes);
        delete.SetAction((parse, ct) => Execute(parse, async (output, ops) =>
        {
            RequireYes(parse, deleteYes, "Deleting a secret");
            var deleted = await ops.DeleteSecretAsync(parse.GetValue(deleteName)!, parse.GetValue(deleteProject), ct);
            output.Write(deleted, s => $"Deleted secret {s.Name} [{s.Scope}].\n");
            return FleetExitCode.Success;
        }, ct));

        secrets.Subcommands.Add(list);
        secrets.Subcommands.Add(set);
        secrets.Subcommands.Add(delete);
        return secrets;
    }

    internal static string ReadSecretValue(string? fromEnv, bool fromStdin, TextReader stdin)
    {
        if (fromEnv is not null == fromStdin)
            throw new FleetUsageException("Pass exactly one of --from-env <VAR> or --stdin. Values are never accepted as arguments.");

        if (fromEnv is not null)
        {
            return Environment.GetEnvironmentVariable(fromEnv) is { Length: > 0 } value
                ? value
                : throw new FleetUsageException($"Environment variable '{fromEnv}' is not set or empty.");
        }

        var text = stdin.ReadToEnd().TrimEnd('\r', '\n');
        return text.Length > 0 ? text : throw new FleetUsageException("No value on standard input.");
    }

    private static Command McpCommand()
    {
        var command = Client(new Command("mcp", "Run an MCP server over stdio exposing these operations to AI agents"));
        command.SetAction(async (parse, ct) =>
        {
            var credentials = ResolveCredentials(parse);
            var session = new FleetSession(credentials, new FleetSettingsFile(FleetSettingsFile.DefaultPath));
            var connection = new CachedConnection(session);
            var server = new FleetMcpServer(new FleetMcpTools(connection.GetAsync, connection.Reset));
            await server.RunAsync(Console.In, Console.Out, ct);
            return FleetExitCode.Success;
        });
        return command;
    }

    /// <summary>One connection for the life of the MCP server, opened again after the token expires.</summary>
    private sealed class CachedConnection(FleetSession session)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private FleetOperations? operations;

        public async Task<FleetOperations> GetAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                return operations ??= new FleetOperations(await session.ConnectAsync(ct));
            }
            finally
            {
                gate.Release();
            }
        }

        public void Reset() => operations = null;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    // The connection options are added to every client command rather than made recursive on the
    // root, because 'fleet coordinator' and 'fleet worker' already own a different --token option.
    private static Command Client(Command command)
    {
        command.Options.Add(UrlOption);
        command.Options.Add(TokenOption);
        command.Options.Add(JsonOption);
        ClientCommands.Add(command);
        return command;
    }

    private static Argument<string> ProjectArgument() => new("project") { Description = "Project name, id or Git URL" };

    private static Argument<string> JobArgument() => new("job") { Description = "Job id or a unique id prefix" };

    private static Option<bool> YesOption(string description = "Confirm the destructive operation") => new("--yes", "-y") { Description = description };

    private static void RequireYes(ParseResult parse, Option<bool> yes, string what)
    {
        if (!parse.GetValue(yes))
            throw new FleetUsageException($"{what} cannot be undone. Pass --yes to confirm.");
    }

    private static FleetCredentials ResolveCredentials(ParseResult parse) =>
        FleetCredentialResolver.Resolve(
            parse.GetValue(UrlOption),
            parse.GetValue(TokenOption),
            Environment.GetEnvironmentVariable,
            new FleetSettingsFile(FleetSettingsFile.DefaultPath).Load());

    private static Task<FleetApiClient> ConnectAsync(ParseResult parse, CancellationToken ct) =>
        new FleetSession(ResolveCredentials(parse), new FleetSettingsFile(FleetSettingsFile.DefaultPath)).ConnectAsync(ct);

    private static async Task<int> Execute(
        ParseResult parse,
        Func<AgentOutput, FleetOperations, Task<int>> body,
        CancellationToken ct,
        bool needsConnection = true)
    {
        var output = new AgentOutput(Console.Out, Console.Error, parse.GetValue(JsonOption));
        try
        {
            var operations = needsConnection ? new FleetOperations(await ConnectAsync(parse, ct)) : null!;
            return await body(output, operations);
        }
        catch (FleetUsageException ex)
        {
            output.Fail(ex.Message, FleetExitCode.UsageOrAuth);
            return FleetExitCode.UsageOrAuth;
        }
        catch (HttpRequestException ex)
        {
            output.Fail(Describe(ex), FleetExitCode.UsageOrAuth);
            return FleetExitCode.UsageOrAuth;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            output.Fail("The coordinator did not answer in time.", FleetExitCode.UsageOrAuth);
            return FleetExitCode.UsageOrAuth;
        }
    }

    internal static string Describe(HttpRequestException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "Not signed in or the token expired. Run 'fleet login'.",
        HttpStatusCode.Forbidden => "This account is not allowed to do that (read-only tokens cannot deploy, change settings or read secret values).",
        HttpStatusCode.NotFound => "Not found on the coordinator (it may be older than this tool).",
        null => $"Cannot reach the coordinator: {ex.Message}",
        _ => ex.Message
    };

    /// <summary>Accepts <c>90s</c>, <c>30m</c>, <c>2h</c>, a number of seconds, or a TimeSpan such as <c>00:30:00</c>.</summary>
    internal static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().ToLowerInvariant();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        var unit = text[^1];
        if (unit is 's' or 'm' or 'h' or 'd'
            && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return unit switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount)
            };
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
            return span;

        throw new FleetUsageException($"Invalid duration '{value}'. Use e.g. 90s, 30m or 2h.");
    }

    private static string? Prompt(string label, bool secret)
    {
        if (Console.IsInputRedirected)
            throw new FleetUsageException($"Cannot prompt for {label.TrimEnd(':', ' ').ToLowerInvariant()}: pass --user and --password-stdin.");

        Console.Error.Write(label);
        if (!secret)
            return Console.ReadLine();

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }

        Console.Error.WriteLine();
        return new string(chars.ToArray());
    }
}
