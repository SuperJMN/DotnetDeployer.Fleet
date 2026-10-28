using System.Net;
using System.Net.Http.Json;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.Dialogs;
using DotnetDeployer.Fleet.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Zafiro.Avalonia.Dialogs;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FleetClientConnectionFlowTests
{
    private const string CorrectPassword = "secret";

    [Fact]
    public async Task Login_WhenPasswordIsWrong_ShouldKeepDialogOpenWithTypedCredentialsAndReason()
    {
        var settings = new InMemorySettingsService();
        (string Username, string Password, string? Error)? shownAfterFailure = null;
        var dialog = new UserDrivenDialog(
            vm => TypeCredentials(vm, "admin", "wrong"),
            vm =>
            {
                var login = (LoginDialogViewModel)vm!;
                shownAfterFailure = (login.Username, login.Password, login.Error);
                return OptionRole.Cancel;
            });
        var flow = CreateFlow(dialog, settings);

        var result = await flow.Login();

        result.HasNoValue.Should().BeTrue();
        shownAfterFailure.Should().Be(("admin", "wrong", "Invalid username or password."));
        settings.GetToken().Should().BeNull();
    }

    [Fact]
    public async Task Login_WhenRetriedWithCorrectPasswordAfterFailure_ShouldCloseAndSucceed()
    {
        var settings = new InMemorySettingsService();
        var dialog = new UserDrivenDialog(
            vm => TypeCredentials(vm, "admin", "wrong"),
            vm => TypeCredentials(vm, "admin", CorrectPassword));
        var flow = CreateFlow(dialog, settings);

        var result = await flow.Login();

        result.Value.IsSuccess.Should().BeTrue();
        dialog.Presses.Should().Be(2);
        settings.GetToken().Should().Be("token");
    }

    [Fact]
    public async Task Connect_WhenEndpointIsInvalid_ShouldKeepDialogOpenWithTypedUrlAndReason()
    {
        var settings = new InMemorySettingsService();
        (string Url, string? Error)? shownAfterFailure = null;
        var dialog = new UserDrivenDialog(
            vm =>
            {
                ((ConnectDialogViewModel)vm!).EndpointUrl = "not a valid url";
                return OptionRole.Primary;
            },
            vm =>
            {
                var connect = (ConnectDialogViewModel)vm!;
                shownAfterFailure = (connect.EndpointUrl, connect.Error);
                return OptionRole.Cancel;
            });
        var flow = CreateFlow(dialog, settings);

        var result = await flow.Connect();

        result.HasNoValue.Should().BeTrue();
        shownAfterFailure!.Value.Url.Should().Be("not a valid url");
        shownAfterFailure.Value.Error.Should().StartWith("Cannot connect:");
        settings.GetEndpoint().Should().BeNull();
    }

    private static OptionRole TypeCredentials(object? viewModel, string username, string password)
    {
        var login = (LoginDialogViewModel)viewModel!;
        login.Username = username;
        login.Password = password;
        return OptionRole.Primary;
    }

    private static FleetClientConnectionFlow CreateFlow(IDialog dialog, ISettingsService settings)
    {
        var handler = new FakeCoordinator();
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");
        var services = new ServiceCollection()
            .AddSingleton(client)
            .AddSingleton(settings)
            .AddTransient<ConnectDialogViewModel>()
            .AddTransient<LoginDialogViewModel>()
            .BuildServiceProvider();

        return new FleetClientConnectionFlow(services, dialog, () => Task.CompletedTask);
    }

    /// <summary>Coordinator that only accepts the correct password.</summary>
    private sealed class FakeCoordinator : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadFromJsonAsync<LoginRequest>(cancellationToken);
            return body?.Password == CorrectPassword
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new FleetApiClient.LoginResponse("token", body.Username, "Admin"))
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        private sealed record LoginRequest(string Username, string Password);
    }
}
