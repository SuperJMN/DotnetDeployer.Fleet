using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Edits a project's polling interval. Manual-only polling is an explicit choice instead of
/// the magic value 0, and an automatic interval must be a whole number of minutes.
/// </summary>
public partial class PollingIntervalViewModel : ReactiveObject
{
    public const int DefaultMinutes = 5;

    [Reactive] private bool _isAutomatic;
    [Reactive] private decimal? _minutes;
    [Reactive] private string? _error;

    public PollingIntervalViewModel(int pollingIntervalMinutes)
    {
        _isAutomatic = pollingIntervalMinutes > 0;
        _minutes = pollingIntervalMinutes > 0 ? pollingIntervalMinutes : DefaultMinutes;

        var validation = this.WhenAnyValue(x => x.IsAutomatic, x => x.Minutes, ToMinutes);
        IsValid = validation.Select(result => result.IsSuccess);
        validation.Subscribe(result => Error = result.IsFailure ? result.Error : null);
    }

    public IObservable<bool> IsValid { get; }

    /// <summary>The interval to persist: 0 for manual only, or the validated number of minutes.</summary>
    public Result<int> ToMinutes() => ToMinutes(IsAutomatic, Minutes);

    private static Result<int> ToMinutes(bool isAutomatic, decimal? minutes)
    {
        if (!isAutomatic)
            return 0;

        if (minutes is not { } value)
            return Result.Failure<int>("Enter the polling interval in minutes.");

        if (value != decimal.Truncate(value) || value < 1 || value > int.MaxValue)
            return Result.Failure<int>("The polling interval must be a whole number of minutes, 1 or more.");

        return (int)value;
    }
}
