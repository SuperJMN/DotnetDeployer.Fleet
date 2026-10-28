using ReactiveUI;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI.Commands;
using DialogOption = Zafiro.Avalonia.Dialogs.Option;

namespace DotnetDeployer.Fleet.App.ViewModels;

internal static class ProjectFormDialog
{
    /// <summary>
    /// Shows the project form in a dialog and returns true once it is saved. Leaving a form with
    /// unsaved changes (Cancel, Escape or the close button) asks first; keeping on editing shows
    /// the same form again, with the changes still there.
    /// </summary>
    public static async Task<bool> ShowProjectForm<TForm>(this IDialog dialog, TForm form, string title)
        where TForm : class, IProjectForm
    {
        while (true)
        {
            var saved = await dialog.Show(form, title, (_, closeable) =>
            {
                var saveCommand = ReactiveCommand.CreateFromTask(async () =>
                {
                    if (await form.TrySaveAsync())
                    {
                        closeable.Close();
                    }
                }, form.CanSave).Enhance();

                return new IOption[]
                {
                    new DialogOption("Cancel",
                        ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                        new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                    new DialogOption("Save",
                        saveCommand,
                        new Settings { IsDefault = true, Role = OptionRole.Primary }),
                };
            });

            if (saved)
                return true;

            if (!form.HasChanges)
                return false;

            var discard = await dialog.ConfirmDestructive(
                "Discard changes?",
                "This project has unsaved changes. Discard them?",
                "Discard",
                cancelText: "Keep Editing");

            if (discard)
                return false;
        }
    }
}
