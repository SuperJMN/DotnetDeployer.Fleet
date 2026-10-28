using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Tooling.Agents;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FleetReferencesTests
{
    private static readonly Project App = new() { Name = "Zafiro.Avalonia.Mcp", GitUrl = "https://github.com/SuperJMN/Zafiro.Avalonia.Mcp.git" };
    private static readonly Project Other = new() { Name = "Other", GitUrl = "https://github.com/acme/other" };
    private static readonly IReadOnlyList<Project> Projects = [App, Other];

    [Theory]
    [InlineData("zafiro.avalonia.mcp")]
    [InlineData("https://github.com/SuperJMN/Zafiro.Avalonia.Mcp")]
    [InlineData("https://github.com/superjmn/zafiro.avalonia.mcp.git/")]
    [InlineData("git@github.com:SuperJMN/Zafiro.Avalonia.Mcp.git")]
    public void Projects_are_found_by_name_or_any_form_of_their_git_url(string reference)
    {
        FleetReferences.ResolveProject(Projects, reference).Should().BeSameAs(App);
    }

    [Fact]
    public void Projects_are_found_by_id()
    {
        FleetReferences.ResolveProject(Projects, Other.Id.ToString()).Should().BeSameAs(Other);
    }

    [Fact]
    public void An_unknown_project_is_a_usage_error()
    {
        var act = () => FleetReferences.ResolveProject(Projects, "missing");

        act.Should().Throw<FleetUsageException>();
    }

    [Fact]
    public void Jobs_are_found_by_a_unique_id_prefix()
    {
        var target = Guid.Parse("4f8639b2-0000-0000-0000-000000000001");
        var other = Guid.Parse("4a000000-0000-0000-0000-000000000002");

        FleetReferences.ResolveJobId([target, other], "4f86").Should().Be(target);
    }

    [Fact]
    public void An_ambiguous_job_prefix_is_rejected()
    {
        var ids = new[] { Guid.Parse("4f8639b2-0000-0000-0000-000000000001"), Guid.Parse("4f8639b3-0000-0000-0000-000000000002") };

        var act = () => FleetReferences.ResolveJobId(ids, "4f8639");

        act.Should().Throw<FleetUsageException>().WithMessage("*matches 2 jobs*");
    }
}
