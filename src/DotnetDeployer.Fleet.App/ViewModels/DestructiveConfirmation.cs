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
    public static Task<bool> ConfirmDestructive(this IDialog dialog, string title, string message, string confirmText, string cancelText = "Cancel") =>
        dialog.Show(new MessageDialogViewModel(message), title, closeable => new IOption[]
        {
            new DialogOption(cancelText,
                ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                new Settings { IsCancel = true, IsDefault = true, Role = OptionRole.Cancel }),
            new DialogOption(confirmText,
                ReactiveCommand.Create(closeable.Close).Enhance(),
                new Settings { Role = OptionRole.Destructive }),
        }, tone: DialogTone.Warning);

    /// <summary>
    /// Asks before queuing a deploy, which publishes to NuGet, GitHub Releases and any other target
    /// enabled in the repository's deployer.yaml and cannot be taken back. Names the project, the
    /// branch and, when polling has seen one, the latest known commit.
    /// </summary>
    public static Task<bool> ConfirmDeploy(this IDialog dialog, Project project)
    {
        var commit = project.LastPolledCommitSha is { Length: > 0 } sha
            ? $"the latest commit on {project.Branch} (last seen: {JobNaming.ShortSha(sha)})"
            : $"the latest commit on {project.Branch}";

        return dialog.ConfirmDestructive(
            "Queue deploy?",
            $"This deploys \"{project.Name}\" from {commit} and publishes it to every target enabled in its deployer.yaml, such as NuGet and GitHub Releases. Published packages and releases cannot be taken back.",
            "Queue Deploy");
    }

    /// <summary>
    /// Tells how many finished jobs (builds and deploys) will go and that active ones stay.
    /// Returns false without asking when there is nothing to clear.
    /// </summary>
    public static async Task<bool> ConfirmClearBuilds(this IDialog dialog, IEnumerable<DeploymentJob> jobs, string scope)
    {
        var finished = jobs.Count(job => FinishedStatuses.Contains(job.Status));
        if (finished == 0)
        {
            await dialog.ShowMessage("Nothing to clear", "There are no finished jobs to clear.");
            return false;
        }

        var finishedJobs = finished == 1 ? "1 finished job" : $"{finished} finished jobs";

        return await dialog.ConfirmDestructive(
            "Clear jobs?",
            $"This deletes {finishedJobs} {scope}, along with their logs. Jobs that are queued or still running are not touched.",
            "Clear Jobs");
    }
}
