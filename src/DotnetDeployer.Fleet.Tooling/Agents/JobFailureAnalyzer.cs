using System.Text.RegularExpressions;
using DotnetDeployer.Fleet.Core.Domain;

namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>Whether a failure comes from the repository's code or from the environment that ran it.</summary>
public enum FailureOrigin
{
    Unknown,
    Code,
    Infrastructure
}

public sealed record BuildError(string Code, string Message, string? Project, string? File, int? Line);

public sealed record FailedTest(string Name, string? Message, IReadOnlyList<string> StackTrace);

public sealed record FailureReport(
    JobView Job,
    string? FailedPhase,
    FailureOrigin Origin,
    string? ErrorMessage,
    IReadOnlyList<BuildError> BuildErrors,
    IReadOnlyList<FailedTest> FailedTests,
    IReadOnlyList<string> Suggestions,
    IReadOnlyList<string> ErrorLines);

/// <summary>
/// Turns hundreds of log lines into what an agent needs to act on a failed job: the phase that
/// failed, MSBuild errors with their code and project, failed tests with message and stack, known
/// remedies, and whether the cause looks like the code or the worker's environment.
/// </summary>
public static partial class JobFailureAnalyzer
{
    private const int MaxStackLines = 5;
    private const int MaxErrorLines = 20;

    // path(line,col): error CODE: message [project]
    [GeneratedRegex(@"^(?:.*?\s)?(?<file>[^\s(][^(]*?)\((?<line>\d+)(?:,\d+)?\)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<project>[^\]]+)\])?\s*$")]
    private static partial Regex FileError();

    // origin : error CODE: message [project]   (no line information, e.g. NETSDK, MSB, XA errors)
    [GeneratedRegex(@"^(?:.*?\s)?(?<origin>[^\s:]+)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<project>[^\]]+)\])?\s*$")]
    private static partial Regex OriginError();

    // VSTest console:  "  Failed Namespace.Type.Method [12 ms]"
    [GeneratedRegex(@"^\s*Failed\s+(?<name>\S.*?)\s+\[[^\]]*\]\s*$")]
    private static partial Regex VsTestFailed();

    // Microsoft.Testing.Platform:  "failed Namespace.Type.Method (12ms)"
    [GeneratedRegex(@"^\s*failed\s+(?<name>\S.*?)\s+\([^)]*\)\s*$")]
    private static partial Regex MtpFailed();

    [GeneratedRegex(@"(?i)(?:secret|environment variable|env var)\s*['""`]?(?<name>[A-Z][A-Z0-9_]{2,})['""`]?\s*(?:is\s+)?(?:not set|missing|not found|not defined|is required|was not provided)")]
    private static partial Regex MissingSecret();

    [GeneratedRegex(@"(?i)missing (?:required )?secret[s]?:?\s*(?<name>[A-Z][A-Z0-9_]{2,})")]
    private static partial Regex MissingSecretPrefix();

    [GeneratedRegex(@"(?i)workload[s]?\s+(?:must be installed|are required|is required|not installed)[^:]*:?\s*(?<ids>[a-z0-9\-, ]+)?")]
    private static partial Regex MissingWorkload();

    // Whole words only: "--enable-exception-handling" or "-exception-model" in a command line are not problems.
    [GeneratedRegex(@"(?i)(?<![-\w])(error|failed|exception)(?![-\w])")]
    private static partial Regex ProblemWord();

    // Summary lines that mention errors only to say there were none ("0 Error(s)", "Failed: 0", localized too).
    [GeneratedRegex(@"(?i)(\b0\s+(errors?|errores|erreurs?|fehler)\b|\b(errors?|errores|failed|con error|fallidas?)\s*:\s*0\b)")]
    private static partial Regex NoProblemSummary();

    // git log output echoed by the worker: "<sha>\t<subject>"
    [GeneratedRegex(@"^\s*[0-9a-f]{7,40}\t")]
    private static partial Regex CommitLine();

    private const int MaxErrorLineLength = 400;

    private static readonly string[] InfrastructureMessages =
    [
        "coordinator restart",
        "worker unresponsive",
        "worker restarted",
        "worker unregistered",
        "no worker claimed",
        "never started it"
    ];

    private static readonly string[] InfrastructureLogSignals =
    [
        "No space left on device",
        "Could not resolve host",
        "Name or service not known",
        "Temporary failure in name resolution",
        "Connection refused",
        "Connection reset by peer",
        "The SSL connection could not be established",
        "Unable to access 'http",
        "Out of memory",
        "Killed"
    ];

    private static readonly HashSet<string> InfrastructurePhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "worker.git.clone",
        "worker.artifacts.upload"
    };

    public static FailureReport Analyze(JobView job, IReadOnlyList<PhaseView> phases, IReadOnlyList<string> logLines)
    {
        var buildErrors = ParseBuildErrors(logLines);
        var failedTests = ParseFailedTests(logLines);
        var failedPhase = phases.LastOrDefault(p => p.Status == PhaseStatus.Fail)?.Name ?? job.CurrentPhase;

        var suggestions = new List<string>();
        var infrastructureHints = 0;

        bool LogContains(string text) => logLines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

        if (buildErrors.Any(e => e.Code is "XA5300" or "XA0031")
            || LogContains("Android SDK directory could not be found")
            || LogContains("Java SDK directory could not be found"))
        {
            suggestions.Add($"The worker{WorkerSuffix(job)} cannot find the Android SDK or JDK: set ANDROID_HOME and JAVA_HOME in the worker's service environment and restart it. 'fleet workers list' shows what each worker reports.");
            infrastructureHints++;
        }

        if (buildErrors.Any(e => e.Code.Equals("NETSDK1147", StringComparison.OrdinalIgnoreCase)) || MissingWorkload().IsMatch(string.Join('\n', logLines)))
        {
            var ids = MissingWorkload().Matches(string.Join('\n', logLines))
                .Select(m => m.Groups["ids"].Value.Trim())
                .FirstOrDefault(v => v.Length > 0);
            suggestions.Add($"A .NET workload is missing on the worker{WorkerSuffix(job)}: run 'dotnet workload install {(string.IsNullOrWhiteSpace(ids) ? "<workload>" : ids)}' there, or 'dotnet workload restore' in the repository.");
            infrastructureHints++;
        }

        var missingSecrets = logLines
            .SelectMany(l => MissingSecret().Matches(l).Concat(MissingSecretPrefix().Matches(l)))
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var secret in missingSecrets)
            suggestions.Add($"Secret '{secret}' looks missing: add it with 'fleet secrets set {secret} --project {job.ProjectName ?? job.ProjectId.ToString()} --from-env {secret}'.");

        if (LogContains("conflicts with the immutable release manifest")
            || job.ErrorMessage?.Contains("conflicts with the immutable release manifest", StringComparison.OrdinalIgnoreCase) == true)
        {
            suggestions.Add("The feed already holds this package version with different content, so the version was reused (by another commit or another publisher). Release a commit that produces a new version; deploying this commit again will not help.");
        }
        else if (LogContains("already exists and cannot be modified")
            || LogContains("409 (Conflict")
            || LogContains("Conflict - The feed already contains")
            || (job.ErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true
                && job.ErrorMessage.Contains("NuGet", StringComparison.OrdinalIgnoreCase)))
        {
            suggestions.Add("NuGet already has this package version: another system may be publishing the same versions ('fleet releases <project>' compares with the feed), or the version did not change since the last release.");
        }

        if (LogContains("No space left on device"))
        {
            suggestions.Add($"The worker{WorkerSuffix(job)} ran out of disk space: free space or lower its repository cache limit.");
            infrastructureHints++;
        }

        if (failedTests.Count > 0)
            suggestions.Add("Tests failed in the release gate: fix them, or disable the gate for this project with 'fleet projects ensure --name <project> --no-tests' if it must ship anyway.");

        var errorMessage = job.ErrorMessage;
        if (errorMessage is not null && InfrastructureMessages.Any(m => errorMessage.Contains(m, StringComparison.OrdinalIgnoreCase)))
            infrastructureHints++;
        if (failedPhase is not null && InfrastructurePhases.Contains(failedPhase))
            infrastructureHints++;
        if (InfrastructureLogSignals.Any(signal => logLines.Any(l => l.Contains(signal, StringComparison.Ordinal))))
            infrastructureHints++;

        var origin = infrastructureHints > 0
            ? FailureOrigin.Infrastructure
            : buildErrors.Count > 0 || failedTests.Count > 0
                ? FailureOrigin.Code
                : FailureOrigin.Unknown;

        var errorLines = logLines
            .Where(l => ProblemWord().IsMatch(l) && !NoProblemSummary().IsMatch(l) && !CommitLine().IsMatch(l))
            .Select(l => l.Length > MaxErrorLineLength ? l[..MaxErrorLineLength] + "…" : l)
            .Distinct()
            .TakeLast(MaxErrorLines)
            .ToList();

        return new FailureReport(job, failedPhase, origin, errorMessage, buildErrors, failedTests, suggestions, errorLines);
    }

    private static string WorkerSuffix(JobView job) =>
        string.IsNullOrWhiteSpace(job.WorkerName) ? "" : $" '{job.WorkerName}'";

    public static IReadOnlyList<BuildError> ParseBuildErrors(IReadOnlyList<string> lines)
    {
        var errors = new List<BuildError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (!line.Contains("error", StringComparison.Ordinal))
                continue;

            BuildError? error = null;
            var fileMatch = FileError().Match(line);
            if (fileMatch.Success)
            {
                error = new BuildError(
                    fileMatch.Groups["code"].Value.ToUpperInvariant(),
                    fileMatch.Groups["msg"].Value.Trim(),
                    ProjectName(fileMatch.Groups["project"].Value),
                    fileMatch.Groups["file"].Value.Trim(),
                    int.Parse(fileMatch.Groups["line"].Value));
            }
            else
            {
                var originMatch = OriginError().Match(line);
                if (originMatch.Success)
                {
                    error = new BuildError(
                        originMatch.Groups["code"].Value.ToUpperInvariant(),
                        originMatch.Groups["msg"].Value.Trim(),
                        ProjectName(originMatch.Groups["project"].Value),
                        null,
                        null);
                }
            }

            // MSBuild repeats every error in its summary; keep the first occurrence.
            if (error is not null && seen.Add($"{error.Code}|{error.File}|{error.Line}|{error.Message}|{error.Project}"))
                errors.Add(error);
        }

        return errors;
    }

    public static IReadOnlyList<FailedTest> ParseFailedTests(IReadOnlyList<string> lines)
    {
        var tests = new List<FailedTest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Count; i++)
        {
            var match = VsTestFailed().Match(lines[i]);
            if (!match.Success) match = MtpFailed().Match(lines[i]);
            if (!match.Success) continue;

            var name = match.Groups["name"].Value.Trim();
            var message = new List<string>();
            var stack = new List<string>();
            var section = "";

            for (var j = i + 1; j < lines.Count; j++)
            {
                var line = lines[j];
                var trimmed = line.Trim();
                if (VsTestFailed().IsMatch(line) || MtpFailed().IsMatch(line)
                    || trimmed.StartsWith("Passed ", StringComparison.Ordinal)
                    || trimmed.StartsWith("Standard Output Messages", StringComparison.Ordinal)
                    || trimmed.StartsWith("Failed!", StringComparison.Ordinal)
                    || trimmed.StartsWith("Total tests", StringComparison.Ordinal))
                    break;

                if (trimmed.StartsWith("Error Message:", StringComparison.Ordinal)) { section = "message"; continue; }
                if (trimmed.StartsWith("Stack Trace:", StringComparison.Ordinal)) { section = "stack"; continue; }
                if (trimmed.Length == 0) { if (section == "stack") break; continue; }

                if (section == "stack" || trimmed.StartsWith("at ", StringComparison.Ordinal))
                {
                    if (stack.Count < MaxStackLines) stack.Add(trimmed);
                    section = "stack";
                }
                else if (section == "message" || section == "")
                {
                    message.Add(trimmed);
                }
            }

            if (seen.Add(name))
                tests.Add(new FailedTest(name, message.Count == 0 ? null : string.Join(Environment.NewLine, message), stack));
        }

        return tests;
    }

    private static string? ProjectName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var path = raw.Split("::", StringSplitOptions.None)[0].Trim();
        return Path.GetFileName(path.Replace('\\', '/'));
    }
}
