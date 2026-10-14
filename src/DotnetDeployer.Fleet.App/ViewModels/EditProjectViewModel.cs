using System.Reactive;
using System.Reactive.Linq;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.App.ViewModels;

public partial class EditProjectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectsViewModel? _projects;
    private readonly Project _project;
    private readonly string _originalToken;

    [Reactive] private string _name;
    [Reactive] private string _gitUrl;
    [Reactive] private string _branch;
    [Reactive] private string _gitToken;
    [Reactive] private bool _runTestsBeforeDeploy;
    [Reactive] private string _expectedPackageIdsText;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public EditProjectViewModel(Project project, FleetApiClient client, INavigator navigator, ProjectsViewModel? projectsForRefresh)
    {
        _project = project;
        _client = client;
        _navigator = navigator;
        _projects = projectsForRefresh;

        _name = project.Name;
        _gitUrl = project.GitUrl;
        _branch = project.Branch;
        Polling = new PollingIntervalViewModel(project.PollingIntervalMinutes);
        _originalToken = project.GitToken ?? string.Empty;
        _gitToken = _originalToken;
        _runTestsBeforeDeploy = project.RunTestsBeforeDeploy;
        _expectedPackageIdsText = string.Join(", ", project.ExpectedPackageIds);

        var canSave = this.WhenAnyValue(
            x => x.Name, x => x.GitUrl, x => x.Branch,
            (n, g, b) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(g) && !string.IsNullOrWhiteSpace(b))
            .CombineLatest(Polling.IsValid, (hasRequiredFields, isPollingValid) => hasRequiredFields && isPollingValid);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canSave);
        CancelCommand = ReactiveCommand.Create(Cancel);
        ClearTokenCommand = ReactiveCommand.Create(() => GitToken = string.Empty);

        SaveCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
    }

    public PollingIntervalViewModel Polling { get; }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, string> ClearTokenCommand { get; }

    private async Task SaveAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var polling = Polling.ToMinutes();
            if (polling.IsFailure)
            {
                Error = polling.Error;
                return;
            }

            // Token semantics on the Update endpoint:
            //   null  => leave unchanged
            //   ""    => clear stored token
            //   value => replace
            // Only send a value when the user actually changed it, to avoid surprises.
            string? tokenToSend = GitToken == _originalToken ? null : GitToken;

            var packageIds = ExpectedPackageIdsText
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await _client.UpdateProjectAsync(
                _project.Id,
                name: Name,
                gitUrl: GitUrl,
                branch: Branch,
                pollingIntervalMinutes: polling.Value,
                gitToken: tokenToSend,
                runTestsBeforeDeploy: RunTestsBeforeDeploy,
                expectedPackageIds: packageIds);

            _projects?.RefreshCommand.Execute(Unit.Default).Subscribe();
            await _navigator.GoBack();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Cancel() => _navigator.GoBack();
}
