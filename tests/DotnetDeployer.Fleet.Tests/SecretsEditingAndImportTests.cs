using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI.Builder;
using Zafiro.DivineBytes;
using Zafiro.UI;

namespace DotnetDeployer.Fleet.Tests;

public sealed class SecretsEditingAndImportTests
{
    static SecretsEditingAndImportTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task Editing_a_global_secret_starts_with_its_value_hidden()
    {
        var api = new SecretsApi();
        using var secrets = GlobalSecrets(api, new ScriptedDialog(confirm: true), [("GITHUB_TOKEN", "old")]);
        var secret = secrets.Secrets.Single();
        await secret.EditCommand.Execute();
        secret.IsValueRevealed = true;
        await secret.CancelCommand.Execute();

        await secret.EditCommand.Execute();

        secret.IsValueRevealed.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_a_global_secret_hides_its_value_again()
    {
        var api = new SecretsApi();
        using var secrets = GlobalSecrets(api, new ScriptedDialog(confirm: true), [("GITHUB_TOKEN", "old")]);
        var secret = secrets.Secrets.Single();
        await secret.EditCommand.Execute();
        secret.IsValueRevealed = true;

        await secret.SaveCommand.Execute();

        secret.IsValueRevealed.Should().BeFalse();
    }

    [Fact]
    public async Task Editing_a_project_secret_starts_with_its_value_hidden()
    {
        var projectId = Guid.NewGuid();
        var api = new SecretsApi();
        var dialog = new ScriptedDialog(confirm: true);
        using var secrets = new ProjectSecretsViewModel(projectId, "App", api.Client,
            Substitute.For<IConnectedFleetClientContext>(), dialog, Substitute.For<INotificationService>());
        var secret = new ProjectSecretViewModel(new Secret { Name = "SIGNING_KEY", Value = "x" }, projectId, api.Client, dialog, secrets);
        secrets.Secrets.Add(secret);
        await secret.EditCommand.Execute();
        secret.IsValueRevealed = true;
        await secret.CancelCommand.Execute();

        await secret.EditCommand.Execute();

        secret.IsValueRevealed.Should().BeFalse();
    }

    [Fact]
    public async Task Importing_lists_what_is_created_and_what_is_overwritten_before_changing_anything()
    {
        var api = new SecretsApi();
        var dialog = new ScriptedDialog(confirm: false);
        using var secrets = GlobalSecrets(api, dialog, [("GITHUB_TOKEN", "old")], EnvFileWith("GITHUB_TOKEN=new\nNUGET_API_KEY=key"));

        await secrets.ImportFromEnvCommand.Execute();

        var confirmation = dialog.Shown.Should().ContainSingle().Subject;
        confirmation.Message.Should().MatchRegex("Creates 1 secret:\\s+• NUGET_API_KEY")
            .And.MatchRegex("Overwrites 1 secret[^:]*:\\s+• GITHUB_TOKEN");
        api.Writes.Should().BeEmpty();
        secrets.Secrets.Select(s => (s.Name, s.Value)).Should().Equal(("GITHUB_TOKEN", "old"));
    }

    [Fact]
    public async Task Confirming_an_import_creates_and_overwrites_and_then_summarises_it()
    {
        var api = new SecretsApi();
        var dialog = new ScriptedDialog(confirm: true);
        using var secrets = GlobalSecrets(api, dialog, [("GITHUB_TOKEN", "old")],
            EnvFileWith("# tokens\nGITHUB_TOKEN=new\nexport NUGET_API_KEY=\"key\""));

        await secrets.ImportFromEnvCommand.Execute();

        api.Writes.Should().BeEquivalentTo(["PUT GITHUB_TOKEN=new", "POST NUGET_API_KEY=key"]);
        secrets.Secrets.Select(s => (s.Name, s.Value)).Should().BeEquivalentTo([("GITHUB_TOKEN", "new"), ("NUGET_API_KEY", "key")]);
        dialog.Shown.Select(shown => shown.Title).Should().Equal("Import secrets?", "Import finished");
        dialog.Shown[^1].Message.Should().Contain("Created 1 secret, overwrote 1 secret.");
    }

    [Fact]
    public async Task The_import_summary_names_the_secrets_the_coordinator_refused()
    {
        var api = new SecretsApi(refuse: "BAD_ONE");
        var dialog = new ScriptedDialog(confirm: true);
        using var secrets = GlobalSecrets(api, dialog, [], EnvFileWith("GOOD_ONE=1\nBAD_ONE=2"));

        await secrets.ImportFromEnvCommand.Execute();

        secrets.Secrets.Select(s => s.Name).Should().Equal("GOOD_ONE");
        dialog.Shown[^1].Message.Should().Contain("Created 1 secret, overwrote 0 secrets.")
            .And.Contain("Could not import 1 secret:").And.Contain("• BAD_ONE");
    }

    [Fact]
    public async Task A_name_repeated_in_the_env_file_is_imported_once_with_its_last_value()
    {
        var api = new SecretsApi();
        using var secrets = GlobalSecrets(api, new ScriptedDialog(confirm: true), [], EnvFileWith("TOKEN=first\nTOKEN=last"));

        await secrets.ImportFromEnvCommand.Execute();

        api.Writes.Should().Equal("POST TOKEN=last");
    }

    [Fact]
    public async Task An_env_file_without_entries_says_so_and_changes_nothing()
    {
        var api = new SecretsApi();
        var dialog = new ScriptedDialog(confirm: true);
        using var secrets = GlobalSecrets(api, dialog, [], EnvFileWith("# only a comment\n\n"));

        await secrets.ImportFromEnvCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Title.Should().Be("Nothing to import");
        api.Writes.Should().BeEmpty();
    }

    private static INamedByteSource EnvFileWith(string content) =>
        new NamedByteSource("secrets.env", ByteSource.FromBytes(Encoding.UTF8.GetBytes(content)));

    private static SecretsViewModel GlobalSecrets(SecretsApi api, ScriptedDialog dialog,
        IEnumerable<(string Name, string Value)> existing, INamedByteSource? envFile = null)
    {
        var picker = Substitute.For<IFileSystemPicker>();
        picker.PickForOpen(Arg.Any<FileTypeFilter[]>())
            .Returns(Task.FromResult(Result.Success(Maybe.From(envFile!))));

        var secrets = new SecretsViewModel(api.Client, Substitute.For<IConnectedFleetClientContext>(),
            picker, dialog, Substitute.For<INotificationService>());
        foreach (var (name, value) in existing)
        {
            secrets.Secrets.Add(new SecretViewModel(new Secret { Name = name, Value = value }, api.Client, dialog, secrets));
        }

        return secrets;
    }

    /// <summary>Coordinator stand-in that records every secret it is asked to write.</summary>
    private sealed class SecretsApi
    {
        public SecretsApi(string? refuse = null)
        {
            var handler = new StubHandler(request =>
            {
                var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
                var name = body.GetProperty("name").GetString()!;
                var value = body.GetProperty("value").GetString()!;
                if (name == refuse)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                Writes.Add($"{request.Method} {name}={value}");
                return request.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new Secret { Name = name, Value = value }) }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            Client = new FleetApiClient(handler, handler);
            Client.SetBaseAddress("http://localhost:5000");
        }

        public FleetApiClient Client { get; }
        public List<string> Writes { get; } = [];
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
