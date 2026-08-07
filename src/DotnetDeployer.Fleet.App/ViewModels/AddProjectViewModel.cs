using DotnetDeployer.Fleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetDeployer.Fleet.App.ViewModels;

public partial class AddProjectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;

    [Reactive] private string _name = string.Empty;
    [Reactive] private string _gitUrl = string.Empty;
    [Reactive] private string _branch = "main";
    [Reactive] private string _pollingInterval = "0";
    [Reactive] private string _gitToken = string.Empty;
    [Reactive] private bool _runTestsBeforeDeploy = true;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public AddProjectViewModel(FleetApiClient client)
    {
        _client = client;

        CanSave = this.WhenAnyValue(
            x => x.Name, x => x.GitUrl, x => x.Branch,
            HasRequiredFields);
    }

    public IObservable<bool> CanSave { get; }

    public async Task<bool> TrySaveAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            if (!HasRequiredFields(Name, GitUrl, Branch))
            {
                Error = "Name, Git URL and Branch are required.";
                return false;
            }

            int polling = int.TryParse(PollingInterval, out var p) ? p : 0;
            var token = string.IsNullOrWhiteSpace(GitToken) ? null : GitToken;
            await _client.CreateProjectAsync(Name, GitUrl, Branch, polling, token, RunTestsBeforeDeploy);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool HasRequiredFields(string name, string gitUrl, string branch) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(gitUrl)
        && !string.IsNullOrWhiteSpace(branch);
}
