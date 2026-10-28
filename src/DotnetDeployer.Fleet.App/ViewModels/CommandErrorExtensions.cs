using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Zafiro.UI;

namespace DotnetDeployer.Fleet.App.ViewModels;

public static class CommandErrorExtensions
{
    /// <summary>
    /// Shows every exception a command throws as a notification. Subscribing to a command's
    /// <c>ThrownExceptions</c> also keeps the exception away from ReactiveUI's default handler,
    /// which would rethrow it and close the app.
    /// </summary>
    public static IDisposable NotifyWith(this IObservable<Exception> exceptions, INotificationService notificationService, string title)
    {
        return exceptions
            .Select(exception => Result.Failure(exception.Message))
            .HandleErrorsWith(notificationService, Maybe.From(title));
    }
}
