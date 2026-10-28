using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// In-memory stand-in for the coordinator's HTTP API, at the network boundary of the client
/// commands. It keeps projects, jobs, logs, workers and secrets, and answers the routes the
/// <c>fleet</c> client uses with the same semantics as the real endpoints.
/// </summary>
internal sealed class FakeCoordinator : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public List<Project> Projects { get; } = [];
    public List<DeploymentJob> Jobs { get; } = [];
    public Dictionary<Guid, List<string>> Logs { get; } = [];
    public List<FleetApiClient.WorkerInfo> Workers { get; } = [];
    public List<Secret> Secrets { get; } = [];
    public List<ProjectRelease> Releases { get; } = [];
    public Dictionary<Guid, string> Heads { get; } = [];
    public List<JobPhase> Phases { get; } = [];
    public List<string> Requests { get; } = [];

    public FleetApiClient CreateClient()
    {
        var client = new FleetApiClient(this, this);
        client.SetBaseAddress("http://fleet.test:5000");
        client.SetToken("test-token");
        return client;
    }

    public Project AddProject(string name, string gitUrl = "https://github.com/acme/app.git")
    {
        var project = new Project { Name = name, GitUrl = gitUrl, Branch = "master" };
        Projects.Add(project);
        return project;
    }

    public DeploymentJob AddJob(Project project, JobStatus status, string? sha = null, params string[] logLines)
    {
        var job = new DeploymentJob
        {
            ProjectId = project.Id,
            Status = status,
            TriggerCommitSha = sha ?? new string('a', 40),
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-Jobs.Count - 1)
        };
        Jobs.Add(job);
        Logs[job.Id] = logLines.ToList();
        return job;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = request.Method.Method;
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        Requests.Add($"{method} {path}");

        return (method, segments) switch
        {
            ("GET", ["api", "auth", "session"]) => Ok(new { username = "agent", role = "Admin" }),
            ("GET", ["api", "projects"]) => Ok(Projects),
            ("POST", ["api", "projects"]) => CreateProject(body!),
            ("GET", ["api", "projects", var id]) => Find(Projects, p => p.Id == Guid.Parse(id)),
            ("PUT", ["api", "projects", var id]) => UpdateProject(Guid.Parse(id), body!),
            ("GET", ["api", "projects", var id, "jobs"]) => Ok(Jobs.Where(j => j.ProjectId == Guid.Parse(id))),
            ("GET", ["api", "projects", var id, "head"]) => Ok(new ProjectHead("master", Heads[Guid.Parse(id)])),
            ("POST", ["api", "projects", var id, "deploy"]) => Deploy(Guid.Parse(id), body),
            ("GET", ["api", "projects", _, "releases"]) => Ok(Releases),
            ("GET", ["api", "projects", var id, "secrets"]) => Ok(Secrets.Where(s => s.ProjectId == Guid.Parse(id))),
            ("POST", ["api", "projects", var id, "secrets"]) => CreateSecret(Guid.Parse(id), body!),
            ("PUT", ["api", "projects", _, "secrets", var secretId]) => UpdateSecret(Guid.Parse(secretId), body!),
            ("GET", ["api", "secrets"]) => Ok(Secrets.Where(s => s.ProjectId is null)),
            ("POST", ["api", "secrets"]) => CreateSecret(null, body!),
            ("PUT", ["api", "secrets", var secretId]) => UpdateSecret(Guid.Parse(secretId), body!),
            ("GET", ["api", "jobs"]) => Ok(Jobs),
            ("GET", ["api", "jobs", var id]) => Find(Jobs, j => j.Id == Guid.Parse(id)),
            ("GET", ["api", "jobs", var id, "phases"]) => Ok(Phases.Where(p => p.JobId == Guid.Parse(id))),
            ("GET", ["api", "jobs", var id, "log-lines"]) => LogLines(Guid.Parse(id), query["since"], query["tail"]),
            ("POST", ["api", "jobs", var id, "cancel"]) => Cancel(Guid.Parse(id)),
            ("GET", ["api", "workers"]) => Ok(Workers),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private HttpResponseMessage CreateProject(JsonObject body)
    {
        var project = new Project
        {
            Name = body["name"]!.GetValue<string>(),
            GitUrl = body["gitUrl"]!.GetValue<string>(),
            Branch = body["branch"]!.GetValue<string>(),
            PollingIntervalMinutes = body["pollingIntervalMinutes"]?.GetValue<int>() ?? 0,
            RunTestsBeforeDeploy = body["runTestsBeforeDeploy"]?.GetValue<bool>() ?? true,
            ExpectedPackageIds = body["expectedPackageIds"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? []
        };
        Projects.Add(project);
        return Respond(HttpStatusCode.Created, project);
    }

    private HttpResponseMessage UpdateProject(Guid id, JsonObject body)
    {
        var project = Projects.Single(p => p.Id == id);
        if (body["name"]?.GetValue<string>() is { } name) project.Name = name;
        if (body["gitUrl"]?.GetValue<string>() is { } url) project.GitUrl = url;
        if (body["branch"]?.GetValue<string>() is { } branch) project.Branch = branch;
        if (body["pollingIntervalMinutes"]?.GetValue<int>() is { } poll) project.PollingIntervalMinutes = poll;
        if (body["runTestsBeforeDeploy"]?.GetValue<bool>() is { } tests) project.RunTestsBeforeDeploy = tests;
        if (body["expectedPackageIds"] is JsonArray ids) project.ExpectedPackageIds = ids.Select(n => n!.GetValue<string>()).ToList();
        return Ok(project);
    }

    // Mirrors ProjectEndpoints.EnqueueDeploy: an active deploy of the same commit is reused unless forced.
    private HttpResponseMessage Deploy(Guid projectId, JsonObject? body)
    {
        var sha = body?["commitSha"]?.GetValue<string>() ?? Heads[projectId];
        var force = body?["force"]?.GetValue<bool>() ?? false;
        var active = Jobs.FirstOrDefault(j => j.ProjectId == projectId && j.TriggerCommitSha == sha
                                              && j.Status is JobStatus.Queued or JobStatus.Assigned or JobStatus.Running);
        if (active is not null && !force)
            return Ok(active);

        var job = new DeploymentJob { ProjectId = projectId, TriggerCommitSha = sha };
        Jobs.Add(job);
        Logs[job.Id] = [];
        return Respond(HttpStatusCode.Created, job);
    }

    private HttpResponseMessage CreateSecret(Guid? projectId, JsonObject body)
    {
        var secret = new Secret
        {
            Name = body["name"]!.GetValue<string>(),
            Value = body["value"]!.GetValue<string>(),
            ProjectId = projectId
        };
        Secrets.Add(secret);
        return Respond(HttpStatusCode.Created, secret);
    }

    private HttpResponseMessage UpdateSecret(Guid id, JsonObject body)
    {
        var secret = Secrets.Single(s => s.Id == id);
        secret.Name = body["name"]!.GetValue<string>();
        secret.Value = body["value"]!.GetValue<string>();
        return Ok(secret);
    }

    private HttpResponseMessage LogLines(Guid jobId, string? since, string? tail)
    {
        var job = Jobs.Single(j => j.Id == jobId);
        var entries = Logs[jobId].Select(line => new LogEntry { JobId = jobId, Line = line }).ToList();
        return Ok(JobLogPage.From(jobId, job.Status, entries,
            since is null ? null : int.Parse(since),
            tail is null ? null : int.Parse(tail)));
    }

    private HttpResponseMessage Cancel(Guid jobId)
    {
        Jobs.Single(j => j.Id == jobId).Status = JobStatus.Cancelled;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static HttpResponseMessage Find<T>(IEnumerable<T> items, Func<T, bool> predicate) =>
        items.FirstOrDefault(predicate) is { } item ? Ok(item) : new HttpResponseMessage(HttpStatusCode.NotFound);

    private static HttpResponseMessage Ok(object value) => Respond(HttpStatusCode.OK, value);

    private static HttpResponseMessage Respond(HttpStatusCode status, object value) =>
        new(status) { Content = JsonContent.Create(value, value.GetType(), options: Json) };
}

/// <summary>Clock the tests move by hand; the waits under test advance it instead of sleeping.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan span) => Now += span;
}
