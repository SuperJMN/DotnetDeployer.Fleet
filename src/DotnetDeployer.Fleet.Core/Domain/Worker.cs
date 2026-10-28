namespace DotnetDeployer.Fleet.Core.Domain;

public class Worker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Hashed shared secret used for worker → coordinator authentication.</summary>
    public string SecretHash { get; set; } = "";

    public WorkerStatus Status { get; set; } = WorkerStatus.Offline;
    public bool IsEmbedded { get; set; } = false;
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Maximum allowed disk usage for cached repos (bytes). 0 = unlimited.</summary>
    public long MaxDiskUsageBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default

    public string? RepoStoragePath { get; set; }

    /// <summary>
    /// Informational version of the running worker binary (e.g. "1.2.3+abcdef0").
    /// Reported by the worker on each heartbeat; null until the first heartbeat
    /// from a version-aware worker arrives.
    /// </summary>
    public string? Version { get; set; }

    // ── Hardware capabilities (reported by the worker at register/heartbeat) ──
    // All values default to 0 / null so legacy workers that don't report them
    // still round-trip safely. The capability-aware selector treats missing
    // values as the least-preferred score.

    /// <summary>Number of logical CPUs reported by <c>Environment.ProcessorCount</c>.</summary>
    public int ProcessorCount { get; set; }

    /// <summary>Total physical RAM in megabytes (best-effort, reported by the worker).</summary>
    public long TotalMemoryMb { get; set; }

    /// <summary>Operating system family (e.g. "Linux", "Windows", "OSX").</summary>
    public string? OperatingSystem { get; set; }

    /// <summary>Process architecture (e.g. "X64", "Arm64", "Arm").</summary>
    public string? Architecture { get; set; }

    /// <summary>Optional human-readable CPU model name.</summary>
    public string? CpuModel { get; set; }

    /// <summary>Build environment (Android SDK, JDK, workloads, display, disk) reported on heartbeat.</summary>
    public WorkerEnvironment? Environment { get; set; }
}
