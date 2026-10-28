using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.Avalonia.Dialogs;
using Zafiro.Avalonia.Dialogs.Views;
using Zafiro.DivineBytes;
using Zafiro.UI;
using Zafiro.UI.Commands;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;
using DialogOption = Zafiro.Avalonia.Dialogs.Option;

namespace DotnetDeployer.Fleet.App.ViewModels;

[Section(name: "Secrets", icon: "mdi-key-outline", sortIndex: 3)]
public partial class SecretsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly IConnectedFleetClientContext clientContext;
    private readonly IFileSystemPicker _picker;
    private readonly IDialog _dialog;
    private readonly INotificationService _notificationService;
    private readonly CompositeDisposable _disposables = [];

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;
    [Reactive] private string _newName = "";
    [Reactive] private string _newValue = "";

    public ObservableCollection<SecretViewModel> Secrets { get; } = [];
    public ListStatus SecretsStatus { get; }

    public SecretsViewModel(
        FleetApiClient client,
        IConnectedFleetClientContext clientContext,
        IFileSystemPicker picker,
        IDialog dialog,
        INotificationService notificationService)
    {
        _client = client;
        this.clientContext = clientContext;
        _picker = picker;
        _dialog = dialog;
        _notificationService = notificationService;
        SecretsStatus = ListStatus.Of(Secrets, this.WhenAnyValue(vm => vm.IsLoading));
        _disposables.Add(SecretsStatus);

        var canAdd = this.WhenAnyValue(x => x.NewName, x => x.NewValue,
            (n, v) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(v));

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync, canAdd);
        ImportFromEnvCommand = ReactiveCommand.CreateFromTask(ImportFromEnvAsync);

        _disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load secrets")));
        _disposables.Add(AddCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message));
        _disposables.Add(ImportFromEnvCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message));

        Header = Observable.Return<object>(new SectionHeader("Secrets",
            "Shared by every project; project secrets override them",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportFromEnvCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task<Maybe<Result>> LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                var secrets = await client.GetSecretsAsync();
                ObservableCollectionSync.Sync(
                    Secrets,
                    secrets,
                    secret => secret.Id,
                    viewModel => viewModel.Secret.Id,
                    secret => new SecretViewModel(secret, client, _dialog, _notificationService, this),
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
        var secret = await _client.CreateSecretAsync(NewName.Trim(), NewValue);
        Secrets.Add(new SecretViewModel(secret, _client, _dialog, _notificationService, this));
        NewName = "";
        NewValue = "";
    }

    private async Task ImportFromEnvAsync()
    {
        Error = null;

        var pickResult = await _picker.PickForOpen(
            new FileTypeFilter("Environment files (*.env)", ["*.env", ".env", "*"]),
            new FileTypeFilter("All files", ["*"]));

        if (pickResult.IsFailure)
        {
            throw new InvalidOperationException(pickResult.Error);
        }

        var maybeFile = pickResult.Value;
        if (maybeFile.HasNoValue)
        {
            return;
        }

        var file = maybeFile.Value;
        var lines = new List<string>();
        await using (var stream = file.ToStream())
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);
            }
        }

        var plan = SecretImportPlan.For(EnvFile.Parse(lines), Secrets.Select(s => s.Secret.Name));
        if (plan.IsEmpty)
        {
            await _dialog.ShowMessage("Nothing to import", $"\"{file.Name}\" has no KEY=value entries.");
            return;
        }

        if (!await ConfirmImport(plan))
        {
            return;
        }

        var summary = await Import(plan);
        await _dialog.ShowMessage("Import finished", summary.Describe(),
            tone: summary.Failed.Count > 0 ? DialogTone.Warning : DialogTone.Information);
    }

    /// <summary>
    /// Shows what the import creates and overwrites. Cancel is the default, so only the import
    /// button goes ahead; it is marked destructive when existing values would be replaced.
    /// </summary>
    private Task<bool> ConfirmImport(SecretImportPlan plan)
    {
        var overwrites = plan.ToOverwrite.Count > 0;
        return _dialog.Show(new MessageDialogViewModel(plan.Describe()), "Import secrets?", closeable => new IOption[]
        {
            new DialogOption("Cancel",
                ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                new Settings { IsCancel = true, IsDefault = true, Role = OptionRole.Cancel }),
            new DialogOption("Import",
                ReactiveCommand.Create(closeable.Close).Enhance(),
                new Settings { Role = overwrites ? OptionRole.Destructive : OptionRole.Primary }),
        }, tone: overwrites ? DialogTone.Warning : DialogTone.Neutral);
    }

    private async Task<SecretImportSummary> Import(SecretImportPlan plan)
    {
        var created = new List<string>();
        var overwritten = new List<string>();
        var failed = new List<(string Name, string Reason)>();

        foreach (var (name, value) in plan.ToOverwrite)
        {
            var existing = Secrets.FirstOrDefault(s => s.Secret.Name == name);
            try
            {
                if (existing is null)
                {
                    Secrets.Add(new SecretViewModel(await _client.CreateSecretAsync(name, value), _client, _dialog, _notificationService, this));
                    created.Add(name);
                    continue;
                }

                await _client.UpdateSecretAsync(existing.Secret.Id, name, value);
                existing.ApplyImportedValue(value);
                overwritten.Add(name);
            }
            catch (Exception ex)
            {
                failed.Add((name, ex.Message));
            }
        }

        foreach (var (name, value) in plan.ToCreate)
        {
            try
            {
                Secrets.Add(new SecretViewModel(await _client.CreateSecretAsync(name, value), _client, _dialog, _notificationService, this));
                created.Add(name);
            }
            catch (Exception ex)
            {
                failed.Add((name, ex.Message));
            }
        }

        return new SecretImportSummary(created, overwritten, failed);
    }

    internal void Remove(SecretViewModel vm) => Secrets.Remove(vm);
}

public partial class SecretViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly IDialog _dialog;
    private readonly SecretsViewModel _parent;

    public Secret Secret { get; private set; }

    [Reactive] private string _name;
    [Reactive] private string _value;
    [Reactive] private bool _isEditing;
    [Reactive] private bool _isSaved;
    [Reactive] private bool _isValueRevealed;

    public SecretViewModel(Secret secret, FleetApiClient client, IDialog dialog, INotificationService notificationService, SecretsViewModel parent)
    {
        Secret = secret;
        _client = client;
        _dialog = dialog;
        _parent = parent;
        _name = secret.Name;
        _value = secret.Value;

        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        EditCommand = ReactiveCommand.Create(Edit);
        CancelCommand = ReactiveCommand.Create(Cancel);

        DeleteCommand.ThrownExceptions.NotifyWith(notificationService, "Cannot delete secret");
        SaveCommand.ThrownExceptions.NotifyWith(notificationService, "Cannot save secret");
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

    /// <summary>Takes a value written by an import, without discarding an edit in progress.</summary>
    internal void ApplyImportedValue(string value)
    {
        Secret.Value = value;
        if (!IsEditing)
        {
            Value = value;
        }
    }

    private void Edit()
    {
        IsValueRevealed = false;
        IsSaved = false;
        IsEditing = true;
    }

    private async Task DeleteAsync()
    {
        var confirmed = await _dialog.ConfirmDestructive(
            "Delete secret?",
            $"Delete the global secret \"{Secret.Name}\"? This cannot be undone.",
            "Delete");
        if (!confirmed)
            return;

        await _client.DeleteSecretAsync(Secret.Id);
        _parent.Remove(this);
    }

    private async Task SaveAsync()
    {
        await _client.UpdateSecretAsync(Secret.Id, Name.Trim(), Value);
        Secret.Name = Name.Trim();
        Secret.Value = Value;
        IsValueRevealed = false;
        IsEditing = false;
        IsSaved = true;
    }

    private void Cancel()
    {
        Name = Secret.Name;
        Value = Secret.Value;
        IsValueRevealed = false;
        IsEditing = false;
    }
}
