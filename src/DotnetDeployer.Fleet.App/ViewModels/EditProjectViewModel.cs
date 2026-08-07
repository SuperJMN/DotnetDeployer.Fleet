using System.Reactive;
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
    [Reactive] private string _pollingInterval;
    [Reactive] private string _gitToken;
    [Reactive] private bool _runTestsBeforeDeploy;
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
        _pollingInterval = project.PollingIntervalMinutes.ToString();
        _originalToken = project.GitToken ?? string.Empty;
        _gitToken = _originalToken;
        _runTestsBeforeDeploy = project.RunTestsBeforeDeploy;

        var canSave = this.WhenAnyValue(
            x => x.Name, x => x.GitUrl, x => x.Branch,
            (n, g, b) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(g) && !string.IsNullOrWhiteSpace(b));

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canSave);
        CancelCommand = ReactiveCommand.Create(Cancel);
        ClearTokenCommand = ReactiveCommand.Create(() => GitToken = string.Empty);

        SaveCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, string> ClearTokenCommand { get; }

    private async Task SaveAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            int polling = int.TryParse(PollingInterval, out var p) ? p : 0;

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
                pollingIntervalMinutes: polling,
                gitToken: tokenToSend,
                runTestsBeforeDeploy: RunTestsBeforeDeploy);

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
