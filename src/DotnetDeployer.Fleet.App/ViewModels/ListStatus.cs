using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// Tells a list that is still loading apart from one that loaded and has nothing, so a section
/// never shows an empty area without saying why.
/// </summary>
public sealed class ListStatus : ReactiveObject, IDisposable
{
    private readonly Func<int> count;
    private readonly CompositeDisposable disposables = [];
    private bool isLoading;
    private bool hasLoaded;

    private ListStatus(INotifyCollectionChanged items, Func<int> count, IObservable<bool> isLoading)
    {
        this.count = count;

        disposables.Add(isLoading.Subscribe(loading =>
        {
            // The list is only known to be empty once a load has run, so the moment before the
            // first load starts counts as loading rather than as an empty list.
            hasLoaded |= this.isLoading && !loading;
            this.isLoading = loading;
            RaiseStateChanged();
        }));

        disposables.Add(Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => items.CollectionChanged += handler,
                handler => items.CollectionChanged -= handler)
            .Subscribe(_ => RaiseStateChanged()));
    }

    public static ListStatus Of<T>(ObservableCollection<T> items, IObservable<bool> isLoading) =>
        new(items, () => items.Count, isLoading);

    /// <summary>Nothing to show yet because the list is still being loaded.</summary>
    public bool IsLoadingFirstItems => (isLoading || !hasLoaded) && count() == 0;

    /// <summary>A load has finished, none is running and the list has no items.</summary>
    public bool IsEmpty => hasLoaded && !isLoading && count() == 0;

    public void Dispose() => disposables.Dispose();

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsLoadingFirstItems));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }
}
