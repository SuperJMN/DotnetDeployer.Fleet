namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// What a project form holds, compared against the values it was opened with to tell
/// whether there are unsaved changes.
/// </summary>
internal sealed record ProjectFormState(
    string Name,
    string GitUrl,
    string Branch,
    string GitToken,
    bool RunTestsBeforeDeploy,
    string ExpectedPackageIdsText,
    bool IsPollingAutomatic,
    decimal? PollingMinutes)
{
    public static List<string> ParsePackageIds(string text) =>
        text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
