using System.Reactive.Linq;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App;
using DotnetDeployer.Fleet.App.ViewModels;
using Zafiro.UI;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Starts_with_the_saved_theme()
    {
        var settings = new InMemorySettingsService();
        settings.SetTheme(AppTheme.Light);

        using var vm = Create(settings, new RecordingThemeApplier());

        vm.SelectedTheme.Should().Be(AppTheme.Light);
    }

    [Fact]
    public void Choosing_a_theme_applies_it_and_remembers_it()
    {
        var settings = new InMemorySettingsService();
        var applier = new RecordingThemeApplier();
        using var vm = Create(settings, applier);

        vm.SelectedTheme = AppTheme.Dark;

        applier.Applied.Should().Equal(AppTheme.Dark);
        settings.GetTheme().Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void Offers_every_theme_including_following_the_system()
    {
        using var vm = Create(new InMemorySettingsService(), new RecordingThemeApplier());

        vm.Themes.Should().Equal(AppTheme.System, AppTheme.Light, AppTheme.Dark);
    }

    [Fact]
    public void Shows_the_current_endpoint()
    {
        using var vm = Create(
            new InMemorySettingsService(),
            new RecordingThemeApplier(),
            endpoint: new Uri("http://pi:5000/"));

        vm.Backend.EndpointText.Should().Be("http://pi:5000");
    }

    [Fact]
    public async Task Changing_the_endpoint_opens_the_connection_flow()
    {
        var connectionFlow = Substitute.For<IFleetClientConnectionFlow>();
        connectionFlow.Connect().Returns(Task.FromResult(Maybe<Result>.None));
        using var vm = Create(new InMemorySettingsService(), new RecordingThemeApplier(), connectionFlow: connectionFlow);

        await vm.ChangeEndpointCommand.Execute();

        await connectionFlow.Received(1).Connect();
    }

    [Fact]
    public async Task A_failed_sign_out_is_reported_as_a_sign_out_failure()
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Failure<FleetApiClient>("Coordinator unreachable"))));
        var notifications = new RecordingNotifications();
        using var vm = Create(new InMemorySettingsService(), new RecordingThemeApplier(), clientContext: context, notifications: notifications);

        await vm.LogoutCommand.Execute();

        (await notifications.Next()).Should().Be(("Sign out failed", "Coordinator unreachable"));
    }

    private static SettingsViewModel Create(
        ISettingsService settings,
        IThemeApplier themeApplier,
        Uri? endpoint = null,
        IFleetClientConnectionFlow? connectionFlow = null,
        IConnectedFleetClientContext? clientContext = null,
        INotificationService? notifications = null)
    {
        var handler = new StubHandler();
        var client = new FleetApiClient(handler, handler, new Subject<System.Reactive.Unit>());
        var bootstrapper = new AppBootstrapper(
            client,
            settings,
            Substitute.For<IBackendHealthMonitor>(),
            clientContext ?? Substitute.For<IConnectedFleetClientContext>(),
            connectionFlow ?? Substitute.For<IFleetClientConnectionFlow>());
        var backend = BackendStatusViewModelTests.Create(BackendStatusViewModelTests.Snapshot(BackendHealth.Healthy, endpoint));

        return new SettingsViewModel(bootstrapper, backend, settings, themeApplier, notifications ?? Substitute.For<INotificationService>());
    }

    private sealed class RecordingThemeApplier : IThemeApplier
    {
        public List<AppTheme> Applied { get; } = [];

        public void Apply(AppTheme theme) => Applied.Add(theme);
    }

    private sealed class RecordingNotifications : INotificationService
    {
        private readonly System.Threading.Channels.Channel<(string Title, string Message)> shown =
            System.Threading.Channels.Channel.CreateUnbounded<(string, string)>();

        public Task Show(string message, Maybe<string> title)
        {
            shown.Writer.TryWrite((title.GetValueOrDefault(""), message));
            return Task.CompletedTask;
        }

        public async Task<(string Title, string Message)> Next() =>
            await shown.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
