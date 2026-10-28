using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDeployer.Fleet.Core.Api;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>
/// Writes command results either as stable JSON (<c>--json</c>) or as short human-readable text.
/// </summary>
public sealed class AgentOutput(TextWriter stdout, TextWriter stderr, bool json)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool Json { get; } = json;
    public TextWriter Out { get; } = stdout;
    public TextWriter Error { get; } = stderr;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public void Write<T>(T value, Func<T, string> text)
    {
        if (Json)
            Out.WriteLine(Serialize(value));
        else
            Out.Write(text(value));
    }

    public void Fail(string message, int exitCode)
    {
        if (Json)
            Out.WriteLine(Serialize(new { error = message, exitCode }));
        else
            Error.WriteLine($"Error: {message}");
    }

    /// <summary>Progress messages go to stderr so they never mix with the result on stdout.</summary>
    public void Progress(string message) => Error.WriteLine(message);
}

/// <summary>Plain-text renderings of the command results.</summary>
public static class AgentText
{
    public static string Projects(IReadOnlyList<ProjectView> projects)
    {
        if (projects.Count == 0) return "No projects.\n";
        var rows = projects.Select(p => new[]
        {
            p.Name,
            Short(p.Id),
            p.Branch,
            p.PollingIntervalMinutes == 0 ? "off" : $"{p.PollingIntervalMinutes}m",
            p.RunTestsBeforeDeploy ? "on" : "off",
            p.ExpectedPackageIds.Count switch { 0 => "-", 1 => p.ExpectedPackageIds[0], var n => $"{n} packages" },
            p.LatestJob is null ? "-" : $"{p.LatestJob.Status} {Short(p.LatestJob.Id)}",
            p.LastDeploy is null ? "-" : $"{p.LastDeploy.Status} {p.LastDeploy.Version ?? ""}".Trim()
        });
        return Table(["NAME", "ID", "BRANCH", "POLL", "TESTS", "PINNED", "LATEST JOB", "LAST DEPLOY"], rows);
    }

    public static string ProjectDetail(ProjectDetailView detail)
    {
        var p = detail.Project;
        var sb = new StringBuilder();
        sb.AppendLine($"{p.Name} ({p.Id})");
        sb.AppendLine($"  Repository:  {p.GitUrl} @ {p.Branch}{(detail.HasGitToken ? " (with access token)" : "")}");
        sb.AppendLine($"  Polling:     {(p.PollingIntervalMinutes == 0 ? "off" : $"every {p.PollingIntervalMinutes} min")}{(detail.LastPolledCommitSha is null ? "" : $", last seen {ShortSha(detail.LastPolledCommitSha)}")}");
        sb.AppendLine($"  Test gate:   {(p.RunTestsBeforeDeploy ? "on" : "off")}");
        sb.AppendLine($"  Pinned:      {(p.ExpectedPackageIds.Count == 0 ? "(any packable project)" : string.Join(", ", p.ExpectedPackageIds))}");
        if (p.LastDeploy is { } last)
            sb.AppendLine($"  Last deploy: {last.Status} {last.Version} {ShortSha(last.CommitSha)} NuGet={last.NuGetVerification?.ToString() ?? "-"} {Time(last.FinishedAt)}");
        if (p.LatestJob is { } job)
            sb.AppendLine($"  Latest job:  {job.Kind} {job.Status} {Short(job.Id)} {ShortSha(job.CommitSha)}");
        sb.AppendLine($"  Secrets:     {(detail.Secrets.Count == 0 ? "(none)" : string.Join(", ", detail.Secrets.Select(s => $"{s.Name} [{s.Scope}]")))}");
        return sb.ToString();
    }

    public static string Ensure(EnsureProjectResult result) =>
        $"{result.Outcome}: {result.Project.Name} ({result.Project.Id})\n"
        + string.Concat(result.Changes.Where(c => c != "created").Select(c => $"  {c}\n"));

    public static string Preflight(ProjectPreflightReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{report.ProjectName}: {(report.Passed ? "ready to deploy" : "NOT ready")}");
        foreach (var check in report.Checks)
            sb.AppendLine($"  [{Badge(check.Severity)}] {check.Name}: {check.Message}");
        if (report.Requirements.Count > 0)
            sb.AppendLine($"  Requires: {string.Join(", ", report.Requirements)}");
        foreach (var worker in report.Workers)
            sb.AppendLine($"  worker {worker.Name}: {(worker.CanBuild ? "can build" : "cannot build")}{(worker.Reasons.Count == 0 ? "" : $" ({string.Join("; ", worker.Reasons)})")}");
        return sb.ToString();
    }

    public static string Deploy(DeployResult result) => result.Outcome switch
    {
        DeployResult.Queued => $"Queued deploy {result.Job.Id} for {ShortSha(result.CommitSha)}.\n",
        DeployResult.AlreadyActive => $"A deploy of {ShortSha(result.CommitSha)} is already {result.Job.Status}: {result.Job.Id}. Nothing queued.\n",
        _ => $"{ShortSha(result.CommitSha)} was already deployed by {result.Job.Id} ({result.Job.Status}). Nothing queued; use --force to deploy it again.\n"
    };

    public static string Jobs(IReadOnlyList<JobView> jobs)
    {
        if (jobs.Count == 0) return "No jobs.\n";
        return Table(["ID", "PROJECT", "KIND", "STATUS", "WORKER", "COMMIT", "VERSION", "PHASE", "DURATION", "NUGET"],
            jobs.Select(j => new[]
            {
                Short(j.Id),
                j.ProjectName ?? Short(j.ProjectId),
                j.Kind.ToString(),
                j.Status.ToString(),
                j.WorkerName ?? "-",
                ShortSha(j.CommitSha),
                j.Version ?? "-",
                j.CurrentPhase ?? "-",
                Duration(j.DurationMs),
                j.NuGetVerification?.ToString() ?? "-"
            }));
    }

    public static string Job(JobDetailView detail)
    {
        var j = detail.Job;
        var sb = new StringBuilder();
        sb.AppendLine($"Job {j.Id}");
        sb.AppendLine($"  Project:  {j.ProjectName ?? j.ProjectId.ToString()}");
        sb.AppendLine($"  Kind:     {j.Kind}{(j.IsAutoTriggered ? " (automatic)" : "")}");
        sb.AppendLine($"  Status:   {j.Status}{(j.CurrentPhase is null ? "" : $" - {j.CurrentPhase}")}");
        sb.AppendLine($"  Worker:   {j.WorkerName ?? (j.WorkerId?.ToString() ?? "-")}");
        sb.AppendLine($"  Commit:   {j.CommitSha ?? "-"}");
        sb.AppendLine($"  Version:  {j.Version ?? "-"}");
        sb.AppendLine($"  Queued:   {Time(j.EnqueuedAt)}  Started: {Time(j.StartedAt)}  Finished: {Time(j.FinishedAt)}  ({Duration(j.DurationMs)})");
        sb.AppendLine($"  NuGet:    {j.NuGetVerification?.ToString() ?? "-"}");
        foreach (var package in detail.Packages)
            sb.AppendLine($"    {package.Id} {package.Version}: {package.State}{(package.Detail is null ? "" : $" - {package.Detail}")}");
        if (j.ErrorMessage is not null)
            sb.AppendLine($"  Error:    {j.ErrorMessage}");
        return sb.ToString();
    }

    public static string JobLine(JobView j) =>
        $"{Short(j.Id)} {j.Status}{(j.CurrentPhase is null ? "" : $" {j.CurrentPhase}")}{(j.WorkerName is null ? "" : $" on {j.WorkerName}")}{(j.NuGetVerification is null ? "" : $" NuGet={j.NuGetVerification}")}";

    public static string Wait(WaitResult result) =>
        result.TimedOut
            ? $"Timed out waiting for {result.Until.ToLowerInvariant()}: job {result.Job.Id} is {result.Job.Status}.\n"
            : $"Job {result.Job.Id} {result.Job.Status}{(result.Job.NuGetVerification is null ? "" : $" (NuGet {result.Job.NuGetVerification})")}.{(result.Job.ErrorMessage is null ? "" : " " + result.Job.ErrorMessage)}\n";

    public static string Phases(IReadOnlyList<PhaseView> phases) =>
        phases.Count == 0
            ? "No phases recorded.\n"
            : Table(["PHASE", "STATUS", "STARTED", "DURATION", "MESSAGE"],
                phases.Select(p => new[] { p.Name, p.Status.ToString(), Time(p.StartedAt), Duration(p.DurationMs), p.Message ?? "" }));

    public static string Artifacts(IReadOnlyList<PackageArtifact> artifacts) =>
        artifacts.Count == 0
            ? "No artifacts.\n"
            : Table(["PATH", "SIZE", "SHA256"], artifacts.Select(a => new[] { a.RelativePath, Size(a.SizeBytes), a.Sha256[..Math.Min(12, a.Sha256.Length)] }));

    public static string Logs(LogsResult logs) =>
        string.Concat(logs.Lines.Select(l => l.Text + "\n"));

    public static string Failure(FailureReport report)
    {
        var sb = new StringBuilder();
        var j = report.Job;
        sb.AppendLine($"Job {j.Id} ({j.ProjectName}) {j.Status} on {j.WorkerName ?? "-"}");
        sb.AppendLine($"  Origin: {report.Origin}{(report.FailedPhase is null ? "" : $", failed in {report.FailedPhase}")}");
        if (report.ErrorMessage is not null)
            sb.AppendLine($"  Error:  {report.ErrorMessage}");
        if (report.BuildErrors.Count > 0)
        {
            sb.AppendLine("  Build errors:");
            foreach (var e in report.BuildErrors.Take(20))
                sb.AppendLine($"    {e.Code} {e.Message}{(e.File is null ? "" : $" ({e.File}{(e.Line is null ? "" : $":{e.Line}")})")}{(e.Project is null ? "" : $" [{e.Project}]")}");
        }

        if (report.FailedTests.Count > 0)
        {
            sb.AppendLine("  Failed tests:");
            foreach (var t in report.FailedTests.Take(20))
            {
                sb.AppendLine($"    {t.Name}");
                if (t.Message is not null) sb.AppendLine($"      {t.Message.Replace("\n", "\n      ")}");
                foreach (var frame in t.StackTrace) sb.AppendLine($"        {frame}");
            }
        }

        if (report.Suggestions.Count > 0)
        {
            sb.AppendLine("  Suggestions:");
            foreach (var s in report.Suggestions) sb.AppendLine($"    - {s}");
        }

        if (report.BuildErrors.Count == 0 && report.FailedTests.Count == 0 && report.ErrorLines.Count > 0)
        {
            sb.AppendLine("  Relevant log lines:");
            foreach (var l in report.ErrorLines) sb.AppendLine($"    {l}");
        }

        return sb.ToString();
    }

    public static string Releases(ReleasesResult result)
    {
        var sb = new StringBuilder();
        if (result.Releases.Count == 0)
            sb.AppendLine($"Fleet has published no NuGet release of {result.ProjectName}.");
        foreach (var r in result.Releases)
        {
            sb.AppendLine($"{r.Version}  {ShortSha(r.CommitSha)}  {Time(r.PreparedAt)}  job {(r.JobId is null ? "(cleared)" : Short(r.JobId.Value))} {r.JobStatus?.ToString() ?? ""}  NuGet={r.Verification?.ToString() ?? "-"}");
            foreach (var p in r.Packages)
                sb.AppendLine($"    {p.Id} {p.Version}: {p.State}");
        }

        foreach (var feed in result.Feed)
        {
            if (feed.Error is not null)
                sb.AppendLine($"Feed check for {feed.PackageId} failed: {feed.Error}");
            else if (feed.NotPublishedByFleet.Count > 0)
                sb.AppendLine($"{feed.PackageId}: {feed.NotPublishedByFleet.Count} version(s) on the feed were not published by Fleet: {string.Join(", ", feed.NotPublishedByFleet.TakeLast(15))}");
            else
                sb.AppendLine($"{feed.PackageId}: every version on the feed was published by Fleet.");
        }

        return sb.ToString();
    }

    public static string Workers(IReadOnlyList<WorkerView> workers)
    {
        if (workers.Count == 0) return "No workers.\n";
        return Table(["NAME", "STATUS", "LAST SEEN", "VERSION", "OS/ARCH", "JOB", "ANDROID", "JAVA", "DISPLAY", "FREE DISK", "WORKLOADS"],
            workers.Select(w => new[]
            {
                w.Name,
                w.Disconnected ? $"{w.Status} (disconnected)" : w.Status.ToString(),
                Time(w.LastSeenAt),
                w.Version?.Split('+')[0] ?? "-",
                $"{w.OperatingSystem ?? "?"}/{w.Architecture ?? "?"}",
                w.CurrentJobId is null ? "-" : $"{Short(w.CurrentJobId.Value)} {w.CurrentJobProject}",
                w.Environment is null ? "?" : w.Environment.AndroidSdkPath is null ? "no" : "yes",
                w.Environment is null ? "?" : w.Environment.JavaHome is null ? "no" : "yes",
                w.Environment?.HasDisplay is { } display ? (display ? "yes" : "no") : "?",
                w.Environment?.FreeDiskMb is { } mb ? Size(mb * 1024 * 1024) : "?",
                w.Environment?.Workloads is { } workloads ? (workloads.Count == 0 ? "-" : string.Join(",", workloads)) : "?"
            }));
    }

    public static string Secrets(IReadOnlyList<SecretView> secrets) =>
        secrets.Count == 0
            ? "No secrets.\n"
            : Table(["NAME", "SCOPE", "UPDATED"], secrets.Select(s => new[] { s.Name, s.Scope, Time(s.UpdatedAt) }));

    public static string Status(StatusView status)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Coordinator: {status.Url ?? "(not configured)"} {(status.Reachable ? "reachable" : "unreachable")}");
        if (status.CoordinatorVersion is not null) sb.AppendLine($"Version:     {status.CoordinatorVersion}");
        sb.AppendLine($"User:        {(status.Username is null ? "(not signed in)" : $"{status.Username} ({status.Role})")}");
        sb.AppendLine($"Credentials: {status.CredentialSource}");
        if (status.Error is not null) sb.AppendLine($"Problem:     {status.Error}");
        return sb.ToString();
    }

    private static string Badge(PreflightSeverity severity) => severity switch
    {
        PreflightSeverity.Ok => "ok",
        PreflightSeverity.Warning => "warn",
        _ => "FAIL"
    };

    public static string Short(Guid id) => id.ToString("N")[..8];

    public static string ShortSha(string? sha) => string.IsNullOrWhiteSpace(sha) ? "-" : sha[..Math.Min(10, sha.Length)];

    public static string Time(DateTimeOffset? value) =>
        value is null ? "-" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Duration(long? ms)
    {
        if (ms is null) return "-";
        var span = TimeSpan.FromMilliseconds(ms.Value);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
            : $"{span.Seconds}s";
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB"
        : bytes >= 1L << 10 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes} B";

    private static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => r[i].Length))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var row in all)
            sb.AppendLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        return sb.ToString();
    }
}
