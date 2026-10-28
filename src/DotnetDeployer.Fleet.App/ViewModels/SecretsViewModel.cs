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
using Zafiro.DivineBytes;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

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

        var canAdd = this.WhenAnyValue(x => x.NewName, x => x.NewValue,
            (n, v) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(v));

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync, canAdd);
        ImportFromEnvCommand = ReactiveCommand.CreateFromTask(ImportFromEnvAsync);

        _disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load secrets")));
        _disposables.Add(AddCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message));
        _disposables.Add(ImportFromEnvCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message));

        Header = Observable.Return<object>(new SectionHeader("Global Secrets",
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

        var parsedEntries = lines
            .Select(ParseEnvLine)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToList();

        var existingByName = Secrets.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var applied = new List<string>();

        foreach (var (name, value) in parsedEntries)
        {
            try
            {
                await ApplyImportedSecret(existingByName, name, value);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PartialImportMessage(applied, parsedEntries.Count, name, ex), ex);
            }

            applied.Add(name);
        }
    }

    private async Task ApplyImportedSecret(Dictionary<string, SecretViewModel> existingByName, string name, string value)
    {
        if (existingByName.TryGetValue(name, out var existing))
        {
            await _client.UpdateSecretAsync(existing.Secret.Id, name, value);
            existing.Name = name;
            existing.Value = value;
            return;
        }

        var created = await _client.CreateSecretAsync(name, value);
        var vm = new SecretViewModel(created, _client, _dialog, _notificationService, this);
        Secrets.Add(vm);
        existingByName[name] = vm;
    }

    private static string PartialImportMessage(IReadOnlyList<string> applied, int total, string failed, Exception error)
    {
        var appliedText = applied.Count == 0
            ? "No secrets were imported."
            : $"Imported {applied.Count} of {total} secrets: {string.Join(", ", applied)}.";
        return $"Import stopped at \"{failed}\": {error.Message.TrimEnd('.')}. {appliedText}";
    }

    private static (string Name, string Value)? ParseEnvLine(string line)
    {
        var text = line.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('#'))
        {
            return null;
        }

        if (text.StartsWith("export ", StringComparison.Ordinal))
        {
            text = text["export ".Length..].TrimStart();
        }

        var separatorIndex = text.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return null;
        }

        var name = text[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var value = text[(separatorIndex + 1)..].Trim();
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                value = value[1..^1];
            }
        }

        return (name, value);
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
        IsEditing = false;
        IsSaved = true;
    }

    private void Edit()
    {
        IsSaved = false;
        IsEditing = true;
    }

    private void Cancel()
    {
        Name = Secret.Name;
        Value = Secret.Value;
        IsEditing = false;
    }
}
