using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectViewModelPollingTextTests
{
    [Fact]
    public void A_project_without_polling_reads_as_manual()
    {
        var vm = Create(new Project { Id = Guid.NewGuid(), Name = "App", PollingIntervalMinutes = 0 });

        vm.PollingText.Should().Be("Manual");
    }

    [Fact]
    public void A_polled_project_shows_its_interval()
    {
        var vm = Create(new Project { Id = Guid.NewGuid(), Name = "App", PollingIntervalMinutes = 15 });

        vm.PollingText.Should().Be("Every 15 min");
    }

    [Fact]
    public void Turning_polling_off_updates_the_card()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App", PollingIntervalMinutes = 5 };
        var vm = Create(project);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplyProjectUpdate(new Project { Id = project.Id, Name = "App", PollingIntervalMinutes = 0 });

        vm.PollingText.Should().Be("Manual");
        changed.Should().Contain(nameof(ProjectViewModel.PollingText));
    }

    // The client, navigator and parent are only used by commands these tests never run.
    private static ProjectViewModel Create(Project project) => new(
        project,
        null!,
        Substitute.For<Zafiro.UI.Navigation.INavigator>(),
        null!,
        Substitute.For<Zafiro.UI.IFileSystemPicker>(),
        Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>(),
        Substitute.For<Zafiro.UI.INotificationService>());
}
