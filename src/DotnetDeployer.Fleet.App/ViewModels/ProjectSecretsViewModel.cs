using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.App.ViewModels;

public partial class ProjectSecretsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly IConnectedFleetClientContext clientContext;
    private readonly Guid _projectId;
    private readonly CompositeDisposable _disposables = [];

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;
    [Reactive] private string _newName = "";
    [Reactive] private string _newValue = "";

    public ObservableCollection<ProjectSecretViewModel> Secrets { get; } = [];

    public ProjectSecretsViewModel(
        Guid projectId,
        string projectName,
        FleetApiClient client,
        IConnectedFleetClientContext clientContext,
        INotificationService notificationService)
    {
        _projectId = projectId;
        _client = client;
        this.clientContext = clientContext;

        var canAdd = this.WhenAnyValue(x => x.NewName, x => x.NewValue,
            (n, v) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(v));

        RefreshCommand = ReactiveCommand.CreateFromTask(Load);
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync, canAdd);

        _disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load project secrets")));
        _disposables.Add(AddCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message));

        Header = Observable.Return<object>(new SectionHeader("Project Secrets",
            $"{projectName} · overrides global secrets",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task<Maybe<Result>> Load()
    {
        IsLoading = true;
        Error = null;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                var secrets = await client.GetProjectSecretsAsync(_projectId);
                ObservableCollectionSync.Sync(
                    Secrets,
                    secrets,
                    secret => secret.Id,
                    viewModel => viewModel.Secret.Id,
                    secret => new ProjectSecretViewModel(secret, _projectId, client, this),
                    (viewModel, secret) => viewModel.ApplySecretUpdate(secret));
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AddAsync()
    {
        var secret = await _client.CreateProjectSecretAsync(_projectId, NewName.Trim(), NewValue);
        Secrets.Add(new ProjectSecretViewModel(secret, _projectId, _client, this));
        NewName = "";
        NewValue = "";
    }

    internal void Remove(ProjectSecretViewModel vm) => Secrets.Remove(vm);
}

public partial class ProjectSecretViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly Guid _projectId;
    private readonly ProjectSecretsViewModel _parent;

    public Secret Secret { get; private set; }

    [Reactive] private string _name;
    [Reactive] private string _value;
    [Reactive] private bool _isEditing;

    public ProjectSecretViewModel(Secret secret, Guid projectId, FleetApiClient client, ProjectSecretsViewModel parent)
    {
        Secret = secret;
        _projectId = projectId;
        _client = client;
        _parent = parent;
        _name = secret.Name;
        _value = secret.Value;

        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        EditCommand = ReactiveCommand.Create(() => { IsEditing = true; });
        CancelCommand = ReactiveCommand.Create(Cancel);

        DeleteCommand.ThrownExceptions.Subscribe(_ => { });
        SaveCommand.ThrownExceptions.Subscribe(_ => { });
    }

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public void ApplySecretUpdate(Secret updated)
    {
        if (updated.Id != Secret.Id) return;

        Secret = updated;
        if (!IsEditing)
        {
            Name = updated.Name;
            Value = updated.Value;
        }

        this.RaisePropertyChanged(nameof(Secret));
    }

    private async Task DeleteAsync()
    {
        await _client.DeleteProjectSecretAsync(_projectId, Secret.Id);
        _parent.Remove(this);
    }

    private async Task SaveAsync()
    {
        await _client.UpdateProjectSecretAsync(_projectId, Secret.Id, Name.Trim(), Value);
        Secret.Name = Name.Trim();
        Secret.Value = Value;
        IsEditing = false;
    }

    private void Cancel()
    {
        Name = Secret.Name;
        Value = Secret.Value;
        IsEditing = false;
    }
}
