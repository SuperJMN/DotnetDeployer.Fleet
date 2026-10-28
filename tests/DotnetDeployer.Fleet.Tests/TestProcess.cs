using System.Diagnostics;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Runs a real child process for a test and keeps everything it printed, so a failing
/// assertion says why the process failed instead of only its exit code.
/// </summary>
internal static class TestProcess
{
    public static ProcessStartInfo StartInfo(string fileName, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public static Task<ProcessOutput> RunAsync(string fileName, string workingDirectory, params string[] arguments) =>
        RunAsync(StartInfo(fileName, workingDirectory, arguments));

    public static ProcessOutput Run(string fileName, string workingDirectory, params string[] arguments) =>
        Run(StartInfo(fileName, workingDirectory, arguments));

    public static async Task<ProcessOutput> RunAsync(ProcessStartInfo startInfo)
    {
        using var process = Start(startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new ProcessOutput(Describe(startInfo), process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public static ProcessOutput Run(ProcessStartInfo startInfo)
    {
        using var process = Start(startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ProcessOutput(Describe(startInfo), process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static Process Start(ProcessStartInfo startInfo)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException($"Could not start {Describe(startInfo)}");
    }

    private static string Describe(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(" ", startInfo.ArgumentList)
            : startInfo.Arguments;
        return $"{startInfo.FileName} {arguments} (in {startInfo.WorkingDirectory})".Trim();
    }
}

internal sealed record ProcessOutput(string Command, int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Everything the process printed, ready to be used as an assertion reason.</summary>
    public string Report =>
        $"{Command} exited with code {ExitCode}{Environment.NewLine}" +
        $"stdout:{Environment.NewLine}{StandardOutput}{Environment.NewLine}" +
        $"stderr:{Environment.NewLine}{StandardError}";

    public ProcessOutput EnsureSuccess()
    {
        if (ExitCode != 0)
            throw new InvalidOperationException(Report);
        return this;
    }
}
