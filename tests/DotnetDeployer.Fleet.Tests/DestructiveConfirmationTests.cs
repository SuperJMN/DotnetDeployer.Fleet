using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI.Builder;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetDeployer.Fleet.Tests;

public sealed class DestructiveConfirmationTests
{
    static DestructiveConfirmationTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task Deleting_a_project_asks_with_its_name_and_does_nothing_when_cancelled()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "DotnetDeployer.Fleet" };
        var api = new RecordingApi();
        var dialog = new ScriptedDialog(confirm: false);
        var vm = new ProjectViewModel(project, api.Client, Substitute.For<INavigator>(), null!,
            Substitute.For<IFileSystemPicker>(), dialog);

        await vm.DeleteCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Message.Should().Contain("\"DotnetDeployer.Fleet\"");
        api.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_project_removes_it_once_confirmed()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App" };
        var api = new RecordingApi();
        var dialog = new ScriptedDialog(confirm: true);
        var parent = new ProjectsViewModel(Substitute.For<IConnectedFleetClientContext>(), Substitute.For<INavigator>(),
            Substitute.For<IFileSystemPicker>(), dialog, Substitute.For<INotificationService>());
        var vm = new ProjectViewModel(project, api.Client, Substitute.For<INavigator>(), parent,
            Substitute.For<IFileSystemPicker>(), dialog);

        await vm.DeleteCommand.Execute();

        api.Deletes.Should().Equal($"/api/projects/{project.Id}");
    }

    [Fact]
    public async Task Cancelling_the_deletion_of_a_global_secret_keeps_it()
    {
        var api = new RecordingApi();
        var dialog = new ScriptedDialog(confirm: false);
        using var secrets = GlobalSecretsWith(api, dialog, "NUGET_API_KEY", out var secret);

        await secret.DeleteCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Message.Should().Contain("\"NUGET_API_KEY\"");
        api.Deletes.Should().BeEmpty();
        secrets.Secrets.Should().ContainSingle();
    }

    [Fact]
    public async Task Confirming_the_deletion_of_a_global_secret_removes_it()
    {
        var api = new RecordingApi();
        using var secrets = GlobalSecretsWith(api, new ScriptedDialog(confirm: true), "NUGET_API_KEY", out var secret);

        await secret.DeleteCommand.Execute();

        api.Deletes.Should().Equal($"/api/secrets/{secret.Secret.Id}");
        secrets.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_deletion_of_a_project_secret_keeps_it()
    {
        var api = new RecordingApi();
        var dialog = new ScriptedDialog(confirm: false);
        using var secrets = ProjectSecretsWith(api, dialog, Guid.NewGuid(), "SIGNING_KEY", out var secret);

        await secret.DeleteCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Message.Should().Contain("\"SIGNING_KEY\"");
        api.Deletes.Should().BeEmpty();
        secrets.Secrets.Should().ContainSingle();
    }

    [Fact]
    public async Task Confirming_the_deletion_of_a_project_secret_removes_it()
    {
        var projectId = Guid.NewGuid();
        var api = new RecordingApi();
        using var secrets = ProjectSecretsWith(api, new ScriptedDialog(confirm: true), projectId, "SIGNING_KEY", out var secret);

        await secret.DeleteCommand.Execute();

        api.Deletes.Should().Equal($"/api/projects/{projectId}/secrets/{secret.Secret.Id}");
        secrets.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task Clearing_project_builds_says_how_many_finished_builds_go_and_that_running_ones_stay()
    {
        var project = AProject();
        var api = new RecordingApi(jobs:
        [
            Job(project.Id, JobStatus.Succeeded),
            Job(project.Id, JobStatus.Failed),
            Job(project.Id, JobStatus.Running)
        ]);
        var dialog = new ScriptedDialog(confirm: false);
        using var vm = ProjectDetail(project, api, dialog);

        await vm.ClearFinishedJobsCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Message.Should()
            .Contain("2 finished builds").And.Contain("still running are not touched");
        api.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirming_clear_builds_deletes_the_finished_builds_of_the_project()
    {
        var project = AProject();
        var api = new RecordingApi(jobs: [Job(project.Id, JobStatus.Succeeded)]);
        using var vm = ProjectDetail(project, api, new ScriptedDialog(confirm: true));

        await vm.ClearFinishedJobsCommand.Execute();

        api.Deletes.Should().Equal($"/api/projects/{project.Id}/jobs/finished");
    }

    [Fact]
    public async Task Cancelling_the_removal_of_a_worker_keeps_it()
    {
        var api = new RecordingApi();
        var dialog = new ScriptedDialog(confirm: false);
        using var workers = WorkersWith(api, dialog, "rpi4", WorkerStatus.Offline, out var worker);

        await worker.RemoveCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Message.Should().Contain("\"rpi4\"");
        api.Deletes.Should().BeEmpty();
        workers.Workers.Should().ContainSingle();
    }

    [Fact]
    public async Task Confirming_the_removal_of_a_worker_unregisters_it()
    {
        var api = new RecordingApi();
        using var workers = WorkersWith(api, new ScriptedDialog(confirm: true), "rpi4", WorkerStatus.Offline, out var worker);

        await worker.RemoveCommand.Execute();

        api.Deletes.Should().Equal($"/api/workers/{worker.Worker.Id}");
        workers.Workers.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_connected_worker_warns_that_it_has_to_be_registered_again()
    {
        var dialog = new ScriptedDialog(confirm: false);
        using var workers = WorkersWith(new RecordingApi(), dialog, "Peach", WorkerStatus.Busy, out var worker);

        await worker.RemoveCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Message.Should()
            .Contain("still connected").And.Contain("registered again");
    }

    private static Project AProject() =>
        new() { Id = Guid.NewGuid(), Name = "App", GitUrl = "https://example.test/repo.git", Branch = "master" };

    private static ProjectDetailViewModel ProjectDetail(Project project, RecordingApi api, ScriptedDialog dialog)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(api.Client))));
        return new ProjectDetailViewModel(project, api.Client, context, Substitute.For<INavigator>(),
            Substitute.For<IFileSystemPicker>(), dialog, Substitute.For<INotificationService>());
    }

    private static SecretsViewModel GlobalSecretsWith(RecordingApi api, ScriptedDialog dialog, string name, out SecretViewModel secret)
    {
        var secrets = new SecretsViewModel(api.Client, Substitute.For<IConnectedFleetClientContext>(),
            Substitute.For<IFileSystemPicker>(), dialog, Substitute.For<INotificationService>());
        secret = new SecretViewModel(new Secret { Id = Guid.NewGuid(), Name = name, Value = "x" }, api.Client, dialog, secrets);
        secrets.Secrets.Add(secret);
        return secrets;
    }

    private static ProjectSecretsViewModel ProjectSecretsWith(RecordingApi api, ScriptedDialog dialog, Guid projectId, string name, out ProjectSecretViewModel secret)
    {
        var secrets = new ProjectSecretsViewModel(projectId, "App", api.Client,
            Substitute.For<IConnectedFleetClientContext>(), dialog, Substitute.For<INotificationService>());
        secret = new ProjectSecretViewModel(new Secret { Id = Guid.NewGuid(), Name = name, Value = "x" }, projectId, api.Client, dialog, secrets);
        secrets.Secrets.Add(secret);
        return secrets;
    }

    private static WorkersViewModel WorkersWith(RecordingApi api, ScriptedDialog dialog, string name, WorkerStatus status, out WorkerItemViewModel worker)
    {
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(api.Client))));
        var workers = new WorkersViewModel(context, dialog, Substitute.For<INotificationService>());
        var info = new FleetApiClient.WorkerInfo(Guid.NewGuid(), name, status, IsEmbedded: false, LastSeenAt: null,
            MaxDiskUsageGb: 10, RepoStoragePath: null);
        worker = new WorkerItemViewModel(info, api.Client, workers);
        workers.Workers.Add(worker);
        return workers;
    }

    private static DeploymentJob Job(Guid projectId, JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Status = status,
        EnqueuedAt = DateTimeOffset.UtcNow
    };

    private sealed class RecordingApi
    {
        public RecordingApi(IReadOnlyList<DeploymentJob>? jobs = null)
        {
            var handler = new StubHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Delete)
                {
                    Deletes.Add(path);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { deleted = 0 }) };
                }

                if (request.Method == HttpMethod.Get && path.EndsWith("/jobs", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(jobs ?? []) };

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            Client = new FleetApiClient(handler, handler);
            Client.SetBaseAddress("http://localhost:5000");
        }

        public FleetApiClient Client { get; }
        public List<string> Deletes { get; } = [];
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
