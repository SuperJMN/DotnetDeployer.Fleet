namespace DotnetDeployer.Fleet.Core.Domain;

public enum PhaseEventKind
{
    Start,
    End,
    Info
}

public enum PhaseStatus
{
    Unknown,
    Ok,
    Fail,
    Waiting
}

/// <summary>
/// A high-level deployment phase event emitted by DotnetDeployer
/// (via <c>##deployer[phase.*]</c> markers) or by the worker itself
/// (e.g. <c>worker.git.clone</c>, <c>worker.deployer.invoke</c>).
/// </summary>
public class PhaseEvent
{
    public PhaseEventKind Kind { get; set; }
    public string Name { get; set; } = "";
    public PhaseStatus Status { get; set; } = PhaseStatus.Unknown;
    public long? DurationMs { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, string> Attrs { get; set; } = new();
}
