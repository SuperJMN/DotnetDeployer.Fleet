using System.Reactive;
using System.Reactive.Linq;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetDeployer.Fleet.App.ViewModels;

public partial class EditProjectViewModel : ReactiveObject, IProjectForm
{
    private readonly FleetApiClient _client;
    private readonly Project _project;
    private readonly string _originalToken;
    private readonly ProjectFormState _initialState;

    [Reactive] private string _name;
    [Reactive] private string _gitUrl;
    [Reactive] private string _branch;
    [Reactive] private string _gitToken;
    [Reactive] private bool _runTestsBeforeDeploy;
    [Reactive] private string _expectedPackageIdsText;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public EditProjectViewModel(Project project, FleetApiClient client)
    {
        _project = project;
        _client = client;

        _name = project.Name;
        _gitUrl = project.GitUrl;
        _branch = project.Branch;
        Polling = new PollingIntervalViewModel(project.PollingIntervalMinutes);
        _originalToken = project.GitToken ?? string.Empty;
        _gitToken = _originalToken;
        _runTestsBeforeDeploy = project.RunTestsBeforeDeploy;
        _expectedPackageIdsText = string.Join(", ", project.ExpectedPackageIds);

        CanSave = this.WhenAnyValue(
                x => x.Name, x => x.GitUrl, x => x.Branch,
                HasRequiredFields)
            .CombineLatest(Polling.IsValid, (hasRequiredFields, isPollingValid) => hasRequiredFields && isPollingValid);

        ClearTokenCommand = ReactiveCommand.Create(() => GitToken = string.Empty);

        _initialState = CurrentState();
    }

    public PollingIntervalViewModel Polling { get; }

    public IObservable<bool> CanSave { get; }

    public bool HasChanges => CurrentState() != _initialState;

    public ReactiveCommand<Unit, string> ClearTokenCommand { get; }

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

            // Token semantics on the Update endpoint:
            //   null  => leave unchanged
            //   ""    => clear stored token
            //   value => replace
            // Only send a value when the user actually changed it, to avoid surprises.
            string? tokenToSend = GitToken == _originalToken ? null : GitToken;

            await _client.UpdateProjectAsync(
                _project.Id,
                name: Name,
                gitUrl: GitUrl,
                branch: Branch,
                pollingIntervalMinutes: polling.Value,
                gitToken: tokenToSend,
                runTestsBeforeDeploy: RunTestsBeforeDeploy,
                expectedPackageIds: ProjectFormState.ParsePackageIds(ExpectedPackageIdsText));

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
