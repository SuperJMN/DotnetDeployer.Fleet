namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Tests that change the test host's environment variables (HOME, ambient secrets...). Child
/// processes inherit that environment, so these tests must never overlap with tests that start
/// processes: xUnit runs a collection with parallelization disabled on its own, after the others.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
