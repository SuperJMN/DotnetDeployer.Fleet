using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Linq;

namespace DotnetDeployer.Fleet.App.ViewModels;

internal static class CollectionChanges
{
    /// <summary>Ticks every time the collection changes.</summary>
    public static IObservable<Unit> Of(INotifyCollectionChanged collection) =>
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => collection.CollectionChanged += handler,
                handler => collection.CollectionChanged -= handler)
            .Select(_ => Unit.Default);
}
