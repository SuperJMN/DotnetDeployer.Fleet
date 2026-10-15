using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using Zafiro.Avalonia.Dialogs;
using Zafiro.Avalonia.Dialogs.Views;
using Zafiro.UI.Commands;
using DialogOption = Zafiro.Avalonia.Dialogs.Option;

namespace DotnetDeployer.Fleet.App.ViewModels;

internal static class DestructiveConfirmation
{
    // Same statuses the coordinator removes when clearing finished jobs.
    private static readonly JobStatus[] FinishedStatuses = [JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled];

    /// <summary>
    /// Asks before an action that cannot be undone. Cancel is both the default and the cancel
    /// option, so Enter, Escape or dismissing the dialog never confirm: only the destructive
    /// button returns true.
    /// </summary>
    public static Task<bool> ConfirmDestructive(this IDialog dialog, string title, string message, string confirmText) =>
        dialog.Show(new MessageDialogViewModel(message), title, closeable => new IOption[]
        {
            new DialogOption("Cancel",
                ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                new Settings { IsCancel = true, IsDefault = true, Role = OptionRole.Cancel }),
            new DialogOption(confirmText,
                ReactiveCommand.Create(closeable.Close).Enhance(),
                new Settings { Role = OptionRole.Destructive }),
        }, tone: DialogTone.Warning);

    /// <summary>
    /// Tells how many finished builds will go and that active ones stay. Returns false without
    /// asking when there is nothing to clear.
    /// </summary>
    public static async Task<bool> ConfirmClearBuilds(this IDialog dialog, IEnumerable<DeploymentJob> jobs, string scope)
    {
        var finished = jobs.Count(job => FinishedStatuses.Contains(job.Status));
        if (finished == 0)
        {
            await dialog.ShowMessage("Nothing to clear", "There are no finished builds to clear.");
            return false;
        }

        var builds = finished == 1 ? "1 finished build" : $"{finished} finished builds";

        return await dialog.ConfirmDestructive(
            "Clear builds?",
            $"This deletes {builds} {scope}, along with their logs. Builds that are queued or still running are not touched.",
            "Clear Builds");
    }
}
