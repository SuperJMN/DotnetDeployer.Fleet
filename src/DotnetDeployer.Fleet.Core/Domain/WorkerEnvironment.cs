namespace DotnetDeployer.Fleet.Core.Domain;

/// <summary>
/// Build environment a worker reports on its heartbeat, so people and agents can tell from
/// outside whether a worker can build a project (for example, whether it sees the Android SDK).
/// Every value is best-effort; null means the worker could not determine it.
/// </summary>
public sealed record WorkerEnvironment
{
    /// <summary>Android SDK directory resolved from <c>ANDROID_HOME</c> or <c>ANDROID_SDK_ROOT</c>.</summary>
    public string? AndroidSdkPath { get; init; }

    /// <summary>JDK directory resolved from <c>JAVA_HOME</c>.</summary>
    public string? JavaHome { get; init; }

    /// <summary>Installed .NET workload ids (e.g. <c>android</c>, <c>wasm-tools</c>).</summary>
    public IReadOnlyList<string>? Workloads { get; init; }

    /// <summary>Whether a graphical display is available to the worker process.</summary>
    public bool? HasDisplay { get; init; }

    /// <summary>Free space, in megabytes, on the volume that holds the worker's repository cache.</summary>
    public long? FreeDiskMb { get; init; }

    public DateTimeOffset ReportedAt { get; init; }

    public bool HasAndroidToolchain => !string.IsNullOrWhiteSpace(AndroidSdkPath) && !string.IsNullOrWhiteSpace(JavaHome);
}
