using System.Xml.Linq;

public class ProjectDetailViewLayoutTests
{
    [Fact]
    public void AppShellView_ShouldNotExposeLogoutAsAGlobalAction()
    {
        var document = XDocument.Load(ProjectFilePath("Shell", "AppShellView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "EnhancedButton")
            .Select(button => button.Attribute("Command")?.Value)
            .Should()
            .NotContain("{Binding LogoutCommand}");

        document.ToString().Should().NotContain("mdi-logout");
    }

    [Fact]
    public void SettingsView_ShouldExposeLogoutAction()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "SettingsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Root!.Attribute(XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml"))!
            .Value.Should().Be("vm:SettingsViewModel");

        document.Descendants(axaml + "EnhancedButton")
            .Where(button => button.Attribute("Command")?.Value == "{Binding LogoutCommand}")
            .Should()
            .ContainSingle()
            .Which.Attribute("Content")?.Value.Should().Be("Sign out");

        document.ToString().Should().Contain("mdi-logout");
    }

    [Fact]
    public void SettingsViewModel_ShouldBeAStandaloneSection()
    {
        var source = File.ReadAllText(ProjectFilePath("ViewModels", "SettingsViewModel.cs"));

        source.Should().Contain("[Section(name: \"Settings\", icon: \"mdi-cog-outline\", sortIndex: 4)]");
        source.Should().Contain("LogoutCommand = ReactiveCommand.CreateFromTask(bootstrapper.Logout)");
        source.Should().Contain("new SectionHeader(\"Settings\"");
    }

    [Fact]
    public void ProjectDetailView_ShouldUseTheFullScreenForJobHistory()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectDetailView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "TabControl").Should().BeEmpty();
        document.Descendants(axaml + "ItemsControl")
            .Select(control => control.Attribute("ItemsSource")?.Value)
            .Should()
            .Contain("{Binding Jobs}");

        document.Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .NotContain(["Project Secrets", "Package Targets"]);
    }

    [Fact]
    public void ProjectDetailView_ShouldGuideUsersWhenThereAreNoBuildsWithoutDuplicatingHeaderActions()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectDetailView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "Border")
            .Select(border => border.Attribute("IsVisible")?.Value)
            .Should()
            .Contain("{Binding ShowEmptyJobHistory}");

        document.Descendants(axaml + "ScrollViewer")
            .Select(scrollViewer => scrollViewer.Attribute("IsVisible")?.Value)
            .Should()
            .Contain("{Binding HasJobHistory}");

        document.Descendants(axaml + "TextBlock")
            .Select(textBlock => textBlock.Attribute("Text")?.Value)
            .Should()
            .Contain([
                "No builds or deployments yet",
                "Use Queue Deploy or Queue Build above to enqueue the first run."
            ]);

        document.Descendants(axaml + "EnhancedButton")
            .Select(button => button.Attribute("Content")?.Value)
            .Should()
            .NotContain(["Queue Deploy", "Queue Build"]);
    }

    [Fact]
    public void BuildsView_ShouldShowAllBuildsWithProjectContext()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "BuildsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "ItemsControl")
            .Select(control => control.Attribute("ItemsSource")?.Value)
            .Should()
            .Contain("{Binding Builds}");

        document.Descendants(axaml + "Run")
            .Select(run => run.Attribute("Text")?.Value)
            .Should()
            .Contain(["{Binding ProjectName}", "{Binding ElapsedText}"]);

        document.Descendants(axaml + "EnhancedButton")
            .Select(button => button.Attribute("Content")?.Value)
            .Should()
            .Contain("View Status");
    }

    [Fact]
    public void ProjectDetailView_ShouldShowElapsedTimeForEachJob()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectDetailView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "Run")
            .Select(run => run.Attribute("Text")?.Value)
            .Should()
            .Contain([" · Elapsed: ", "{Binding ElapsedText}"]);
    }

    [Fact]
    public void JobDetailView_ShouldShowLiveElapsedTime()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "JobDetailView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "TextBlock")
            .Select(textBlock => textBlock.Attribute("Text")?.Value)
            .Should()
            .Contain(["Elapsed:", "{Binding ElapsedText}"]);
    }

    [Fact]
    public void BuildsViewModel_ShouldBeAStandaloneSectionOverAllJobs()
    {
        var source = File.ReadAllText(ProjectFilePath("ViewModels", "BuildsViewModel.cs"));

        source.Should().Contain("[Section(name: \"Builds\", icon: \"mdi-history\", sortIndex: 1)]");
        source.Should().Contain("client.GetAllJobsAsync()");
        source.Should().Contain("OrderByDescending(job => job.EnqueuedAt)");
        source.Should().NotContain("GetProjectJobsAsync");
    }

    [Fact]
    public void ProjectsView_ShouldChangeIconFromTheProjectIcon()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        var iconButtons = document.Descendants(axaml + "EnhancedButton")
            .Where(button => button.Attribute("Command")?.Value == "{Binding ChangeIconCommand}")
            .ToList();

        iconButtons.Should().ContainSingle();
        var iconButton = iconButtons.Single();
        iconButton.Attribute("Width")?.Value.Should().Be("56");
        iconButton.Attribute("Height")?.Value.Should().Be("56");
        iconButton.Attribute("ToolTip.Tip")?.Value.Should().Be("Change icon");
        iconButton.Descendants(axaml + "Image")
            .Select(image => image.Attribute("IsVisible")?.Value)
            .Should()
            .Contain("{Binding HasProjectIcon}");
    }

    [Fact]
    public void ProjectsView_ShouldKeepProjectActionsTogether()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        var actionRows = document.Descendants(axaml + "FlexPanel")
            .Where(panel => panel.Attribute("HorizontalAlignment")?.Value == "Right")
            .Where(panel => panel.Attribute("VerticalAlignment")?.Value == "Bottom")
            .Where(panel => panel.Attribute("JustifyContent")?.Value == "End")
            .ToList();

        actionRows.Should().ContainSingle();
        actionRows.Single()
            .Descendants(axaml + "EnhancedButton")
            .Select(button => button.Attribute("Command")?.Value)
            .Should()
            .Contain(["{Binding ResetIconCommand}", "{Binding EditCommand}", "{Binding DeleteCommand}"]);
        actionRows.Single()
            .Descendants(axaml + "EnhancedButton")
            .Single(button => button.Attribute("Command")?.Value == "{Binding ResetIconCommand}")
            .Attribute("ToolTip.Tip")?.Value
            .Should()
            .Be("Use automatic icon");

        var openCards = document.Descendants(axaml + "EnhancedButton")
            .Where(button => button.Attribute("Command")?.Value == "{Binding OpenCommand}")
            .ToList();

        openCards.Should().ContainSingle();
        var openCard = openCards.Single();
        openCard.Attribute("Content")?.Value.Should().BeNull();
        openCard.Attribute("ToolTip.Tip")?.Value.Should().Be("Open project");
        openCard.Attribute("HorizontalContentAlignment")?.Value.Should().Be("Stretch");

        document.Descendants(axaml + "EnhancedButton")
            .Where(button => button.Attribute("Content")?.Value == "Open")
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void PackageBuildOptionsDialog_ShouldExposeBuildTargetOptions()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "PackageBuildOptionsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .Contain("Package Targets");

        document.Descendants(axaml + "DataTemplate")
            .Select(template => template.Attribute("DataType")?.Value)
            .Should()
            .Contain(["vm:PackagePlatformViewModel", "vm:PackageFormatViewModel", "vm:PackageTargetOptionViewModel"]);

        document.Descendants(axaml + "CheckBox")
            .Select(checkBox => checkBox.Attribute("IsChecked")?.Value)
            .Should()
            .Contain("{Binding IsSelected}");
    }

    [Fact]
    public void ProjectSecretsView_ShouldBeAvailableAsFlyoutContent()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectSecretsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Root!.Attribute(XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml"))!
            .Value.Should().Be("vm:ProjectSecretsViewModel");

        document.Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .Contain("Project Secrets");
    }

    [Theory]
    [InlineData("AddProjectView.axaml")]
    [InlineData("EditProjectView.axaml")]
    public void ProjectFormViews_ShouldFitNarrowScreens(string viewFile)
    {
        var document = XDocument.Load(ProjectFilePath("Views", viewFile));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "ScrollViewer")
            .Select(scrollViewer => scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value)
            .Should()
            .Contain("Disabled");

        document.Descendants()
            .Where(element => element.Attribute("Width")?.Value == "480")
            .Should()
            .BeEmpty();

        document.Descendants(axaml + "StackPanel")
            .Select(stackPanel => stackPanel.Attribute("Orientation")?.Value)
            .Should()
            .NotContain("Horizontal");
    }

    [Fact]
    public void AddProjectView_ShouldLeaveDialogChromeToDialogService()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "AddProjectView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "EnhancedButton").Should().BeEmpty();
        document.Descendants(axaml + "TextBlock")
            .Select(textBlock => textBlock.Attribute("Text")?.Value)
            .Should()
            .NotContain("Add Project");
    }

    [Fact]
    public void ProjectsViewModel_ShouldOpenAddProjectInDialog()
    {
        var source = File.ReadAllText(ProjectFilePath("ViewModels", "ProjectsViewModel.cs"));

        source.Should().Contain("_dialog.Show(vm, \"Add Project\"");
        source.Should().NotContain("Navigator.Go(() => new AddProjectViewModel");
    }

    private static string ProjectFilePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var pathParts = new[] { directory.FullName, "src", "DotnetDeployer.Fleet.App" }.Concat(parts).ToArray();
            var path = Path.Combine(pathParts);
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} from the test output directory.");
    }
}
