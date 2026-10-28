using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Text;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.Builder;
using Zafiro.DivineBytes;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

/// <summary>
/// Commands that talk to the coordinator must tell the user when they fail instead of
/// swallowing the error or letting it reach ReactiveUI's default handler (which closes the app).
/// </summary>
public sealed class CommandErrorReportingTests
{
    private const string NetworkError = "Coordinator unreachable";

    static CommandErrorReportingTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task Failing_to_save_a_global_secret_is_reported_and_keeps_the_row_in_edit_mode()
    {
        var notifications = new RecordingNotifications();
        var api = Api.Failing();
        var secret = GlobalSecret(api, notifications);
        secret.EditCommand.Execute().Subscribe();

        await ExecuteIgnoringErrors(secret.SaveCommand);

        (await notifications.Next()).Should().Be(("Cannot save secret", NetworkError));
        secret.IsEditing.Should().BeTrue();
        secret.IsSaved.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_a_global_secret_confirms_it_until_it_is_edited_again()
    {
        var secret = GlobalSecret(Api.Succeeding(), new RecordingNotifications());
        secret.EditCommand.Execute().Subscribe();

        await secret.SaveCommand.Execute();
        var savedAfterSave = secret.IsSaved;
        secret.EditCommand.Execute().Subscribe();

        savedAfterSave.Should().BeTrue();
        secret.IsSaved.Should().BeFalse();
    }

    [Fact]
    public async Task Failing_to_delete_a_global_secret_is_reported_and_keeps_it()
    {
        var notifications = new RecordingNotifications();
        var api = Api.Failing();
        var secrets = new SecretsViewModel(api.Client, Substitute.For<IConnectedFleetClientContext>(),
            Substitute.For<IFileSystemPicker>(), new ScriptedDialog(confirm: true), notifications);
        var secret = new SecretViewModel(ASecret("TOKEN"), api.Client, new ScriptedDialog(confirm: true), notifications, secrets);
        secrets.Secrets.Add(secret);

        await ExecuteIgnoringErrors(secret.DeleteCommand);

        (await notifications.Next()).Should().Be(("Cannot delete secret", NetworkError));
        secrets.Secrets.Should().ContainSingle();
    }

    [Fact]
    public async Task Failing_to_save_a_project_secret_is_reported()
    {
        var notifications = new RecordingNotifications();
        var secret = ProjectSecret(Api.Failing(), notifications);
        secret.EditCommand.Execute().Subscribe();

        await ExecuteIgnoringErrors(secret.SaveCommand);

        (await notifications.Next()).Should().Be(("Cannot save project secret", NetworkError));
        secret.IsEditing.Should().BeTrue();
    }

    [Fact]
    public async Task Failing_to_delete_a_project_secret_is_reported()
    {
        var notifications = new RecordingNotifications();
        var secret = ProjectSecret(Api.Failing(), notifications);

        await ExecuteIgnoringErrors(secret.DeleteCommand);

        (await notifications.Next()).Should().Be(("Cannot delete project secret", NetworkError));
    }

    [Fact]
    public async Task Saving_a_project_secret_confirms_it()
    {
        var secret = ProjectSecret(Api.Succeeding(), new RecordingNotifications());
        secret.EditCommand.Execute().Subscribe();

        await secret.SaveCommand.Execute();

        secret.IsSaved.Should().BeTrue();
        secret.IsEditing.Should().BeFalse();
    }

    [Fact]
    public async Task Failing_to_delete_a_project_is_reported()
    {
        var notifications = new RecordingNotifications();
        var project = Project(Api.Failing(), notifications);

        await ExecuteIgnoringErrors(project.DeleteCommand);

        (await notifications.Next()).Should().Be(("Cannot delete project", NetworkError));
    }

    [Fact]
    public async Task Failing_to_reset_a_project_icon_is_reported()
    {
        var notifications = new RecordingNotifications();
        var project = Project(Api.Failing(), notifications);

        await ExecuteIgnoringErrors(project.ResetIconCommand);

        (await notifications.Next()).Should().Be(("Cannot reset project icon", NetworkError));
    }

    [Fact]
    public async Task Failing_to_change_a_project_icon_is_reported()
    {
        var notifications = new RecordingNotifications();
        var picker = Substitute.For<IFileSystemPicker>();
        picker.PickForOpen(Arg.Any<FileTypeFilter[]>())
            .Returns(Task.FromResult(Result.Success(Maybe.From<INamedByteSource>(
                new NamedByteSource("icon.png", ByteSource.FromBytes([1, 2, 3]))))));
        var project = Project(Api.Failing(), notifications, picker);

        await ExecuteIgnoringErrors(project.ChangeIconCommand);

        (await notifications.Next()).Should().Be(("Cannot change project icon", NetworkError));
    }

    [Fact]
    public async Task Failing_to_save_worker_settings_is_reported()
    {
        var notifications = new RecordingNotifications();
        var worker = Worker(Api.Failing(), notifications);
        worker.MaxDiskUsageGb = 20;

        await ExecuteIgnoringErrors(worker.SaveConfigCommand);

        (await notifications.Next()).Should().Be(("Cannot save worker settings", NetworkError));
        worker.IsSaved.Should().BeFalse();
        worker.IsSaving.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_worker_settings_confirms_it_until_the_value_changes_again()
    {
        var worker = Worker(Api.Succeeding(), new RecordingNotifications());
        worker.MaxDiskUsageGb = 20;

        await worker.SaveConfigCommand.Execute();
        var savedAfterSave = worker.IsSaved;
        worker.MaxDiskUsageGb = 30;

        savedAfterSave.Should().BeTrue();
        worker.IsSaved.Should().BeFalse();
    }

    [Fact]
    public async Task Failing_to_load_the_logs_of_a_finished_job_is_reported()
    {
        var notifications = new RecordingNotifications();
        var api = Api.Failing();
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(api.Client))));
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Status = JobStatus.Succeeded,
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        using var vm = new JobDetailViewModel(job, api.Client, context, Substitute.For<INavigator>(),
            notificationService: notifications);

        (await notifications.Next()).Should().Be(("Cannot load logs", NetworkError));
    }

    private static SecretViewModel GlobalSecret(Api api, INotificationService notifications)
    {
        var secrets = new SecretsViewModel(api.Client, Substitute.For<IConnectedFleetClientContext>(),
            Substitute.For<IFileSystemPicker>(), new ScriptedDialog(confirm: true), notifications);
        var secret = new SecretViewModel(ASecret("TOKEN"), api.Client, new ScriptedDialog(confirm: true), notifications, secrets);
        secrets.Secrets.Add(secret);
        return secret;
    }

    private static ProjectSecretViewModel ProjectSecret(Api api, INotificationService notifications)
    {
        var projectId = Guid.NewGuid();
        var secrets = new ProjectSecretsViewModel(projectId, "App", api.Client,
            Substitute.For<IConnectedFleetClientContext>(), new ScriptedDialog(confirm: true), notifications);
        var secret = new ProjectSecretViewModel(ASecret("SIGNING_KEY"), projectId, api.Client,
            new ScriptedDialog(confirm: true), notifications, secrets);
        secrets.Secrets.Add(secret);
        return secret;
    }

    private static ProjectViewModel Project(Api api, INotificationService notifications, IFileSystemPicker? picker = null)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App" };
        return new ProjectViewModel(project, api.Client, Substitute.For<INavigator>(), null!,
            picker ?? Substitute.For<IFileSystemPicker>(), new ScriptedDialog(confirm: true), notifications);
    }

    private static WorkerItemViewModel Worker(Api api, INotificationService notifications)
    {
        var info = new FleetApiClient.WorkerInfo(Guid.NewGuid(), "rpi4", WorkerStatus.Online, IsEmbedded: false,
            LastSeenAt: null, MaxDiskUsageGb: 10, RepoStoragePath: null);
        return new WorkerItemViewModel(info, api.Client, null!, notifications);
    }

    private static Secret ASecret(string name) => new() { Id = Guid.NewGuid(), Name = name, Value = "x" };

    private static async Task ExecuteIgnoringErrors<TOutput>(ReactiveUI.ReactiveCommand<System.Reactive.Unit, TOutput> command)
    {
        await command.Execute()
            .Select(_ => System.Reactive.Unit.Default)
            .Catch(Observable.Return(System.Reactive.Unit.Default))
            .LastOrDefaultAsync();
    }

    private static string Body(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";

    private static HttpResponseMessage Created(HttpRequestMessage request)
    {
        var name = System.Text.Json.JsonDocument.Parse(Body(request)).RootElement.GetProperty("name").GetString()!;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new Secret { Id = Guid.NewGuid(), Name = name, Value = "v" })
        };
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

    private sealed class Api
    {
        public Api(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var handler = new StubHandler(respond);
            Client = new FleetApiClient(handler, handler);
            Client.SetBaseAddress("http://localhost:5000");
        }

        public FleetApiClient Client { get; }

        public static Api Failing() => new(_ => throw new HttpRequestException(NetworkError));

        public static Api Succeeding() => new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
