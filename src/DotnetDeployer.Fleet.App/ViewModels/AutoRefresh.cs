using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Reloads a list periodically while its view is on screen, so job and worker states change
/// without the user pressing Refresh. The view starts it when attached and stops it when detached.
/// A refresh that is still running makes the next ticks skip, and failures are left for the next
/// tick or a manual refresh to report.
/// </summary>
public sealed class AutoRefresh : IDisposable
{
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(5);

    private readonly Func<Task> refresh;
    private readonly IScheduler scheduler;
    private readonly TimeSpan period;
    private readonly SerialDisposable ticks = new();
    private bool isRefreshing;

    public AutoRefresh(Func<Task> refresh, IScheduler? scheduler = null, TimeSpan? period = null)
    {
        this.refresh = refresh;
        this.scheduler = scheduler ?? RxSchedulers.MainThreadScheduler;
        this.period = period ?? DefaultPeriod;

        StartCommand = ReactiveCommand.Create(Start);
        StopCommand = ReactiveCommand.Create(Stop);
    }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    public void Start()
    {
        ticks.Disposable = Observable.Interval(period, scheduler)
            .Where(_ => !isRefreshing)
            .Select(_ => Observable.FromAsync(RefreshOnce))
            .Concat()
            .Subscribe();
    }

    public void Stop() => ticks.Disposable = Disposable.Empty;

    public void Dispose() => ticks.Dispose();

    private async Task RefreshOnce()
    {
        isRefreshing = true;
        try
        {
            await refresh();
        }
        catch
        {
            // Background refreshes stay silent; the list keeps its last known state.
        }
        finally
        {
            isRefreshing = false;
        }
    }
}
