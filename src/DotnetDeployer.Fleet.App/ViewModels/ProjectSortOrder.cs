namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>How the project list is ordered.</summary>
public enum ProjectSortOrder
{
    /// <summary>Most recent deploy first; projects never deployed go last.</summary>
    LastDeploy,

    /// <summary>Most recent job of any kind first, counting jobs still in progress.</summary>
    RecentActivity,

    /// <summary>Alphabetically by name.</summary>
    Name
}

/// <summary>An entry of the project list's sort selector.</summary>
public sealed record ProjectSortOption(ProjectSortOrder Order, string Label)
{
    public static IReadOnlyList<ProjectSortOption> All { get; } =
    [
        new(ProjectSortOrder.LastDeploy, "Last deploy"),
        new(ProjectSortOrder.RecentActivity, "Recent activity"),
        new(ProjectSortOrder.Name, "Name")
    ];

    public static ProjectSortOption For(ProjectSortOrder order) => All.First(option => option.Order == order);

    public override string ToString() => Label;
}
