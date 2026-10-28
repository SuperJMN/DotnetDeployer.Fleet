using System.Reactive.Linq;
using DotnetDeployer.Fleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetDeployer.Fleet.App.ViewModels;

public partial class AddProjectViewModel : ReactiveObject, IProjectForm
{
    private readonly FleetApiClient _client;
    private readonly ProjectFormState _initialState;

    [Reactive] private string _name = string.Empty;
    [Reactive] private string _gitUrl = string.Empty;
    [Reactive] private string _branch = "main";
    [Reactive] private string _gitToken = string.Empty;
    [Reactive] private bool _runTestsBeforeDeploy = true;
    [Reactive] private string _expectedPackageIdsText = string.Empty;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public AddProjectViewModel(FleetApiClient client)
    {
        _client = client;

        CanSave = this.WhenAnyValue(
                x => x.Name, x => x.GitUrl, x => x.Branch,
                HasRequiredFields)
            .CombineLatest(Polling.IsValid, (hasRequiredFields, isPollingValid) => hasRequiredFields && isPollingValid);

        _initialState = CurrentState();
    }

    public PollingIntervalViewModel Polling { get; } = new(0);

    public IObservable<bool> CanSave { get; }

    public bool HasChanges => CurrentState() != _initialState;

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

            var polling = Polling.ToMinutes();
            if (polling.IsFailure)
            {
                Error = polling.Error;
                return false;
            }

            var token = string.IsNullOrWhiteSpace(GitToken) ? null : GitToken;
            var packageIds = ProjectFormState.ParsePackageIds(ExpectedPackageIdsText);

            await _client.CreateProjectAsync(Name, GitUrl, Branch, polling.Value, token, RunTestsBeforeDeploy, packageIds);
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

    private ProjectFormState CurrentState() =>
        new(Name, GitUrl, Branch, GitToken, RunTestsBeforeDeploy, ExpectedPackageIdsText, Polling.IsAutomatic, Polling.Minutes);

    private static bool HasRequiredFields(string name, string gitUrl, string branch) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(gitUrl)
        && !string.IsNullOrWhiteSpace(branch);
}
