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
    public async Task Cancelling_queue_deploy_queues_nothing()
    {
        var project = AProject();
        var api = new RecordingApi();
        using var vm = ProjectDetail(project, api, new ScriptedDialog(confirm: false));

        await vm.DeployCommand.Execute();

        api.Posts.Should().BeEmpty();
        vm.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Queue_deploy_names_the_project_branch_known_commit_and_publishing_targets()
    {
        var project = AProject();
        project.LastPolledCommitSha = "0123456789abcdef";
        var dialog = new ScriptedDialog(confirm: false);
        using var vm = ProjectDetail(project, new RecordingApi(), dialog);

        await vm.DeployCommand.Execute();

        dialog.Shown.Should().ContainSingle().Which.Message.Should()
            .Contain("\"App\"").And.Contain("master").And.Contain("0123456")
            .And.Contain("NuGet").And.Contain("GitHub Releases").And.Contain("cannot be taken back");
    }

    [Fact]
    public async Task Confirming_queue_deploy_queues_the_deploy_of_the_project()
    {
        var project = AProject();
        var api = new RecordingApi();
        using var vm = ProjectDetail(project, api, new ScriptedDialog(confirm: true));

        await vm.DeployCommand.Execute();

        api.Posts.Should().Equal($"/api/projects/{project.Id}/deploy");
        vm.Jobs.Should().ContainSingle().Which.KindText.Should().Be("Deploy");
    }

    [Fact]
    public async Task Project_header_sets_queue_deploy_apart_from_queue_build()
    {
        using var vm = ProjectDetail(AProject(), new RecordingApi(), new ScriptedDialog(confirm: false));

        var header = (SectionHeader)await vm.Header.FirstAsync();
        var deploy = header.Actions.Single(action => action.Text == "Queue Deploy");
        var build = header.Actions.Single(action => action.Text == "Queue Build");

        deploy.IsDestructiveCommand.Should().BeTrue();
        build.IsPrimaryCommand.Should().BeTrue();
        header.Actions.Where(action => action.IsDestructiveCommand).Should().ContainSingle().Which.Should().BeSameAs(deploy);
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

                if (request.Method == HttpMethod.Post && path.EndsWith("/deploy", StringComparison.Ordinal))
                {
                    Posts.Add(path);
                    var job = new DeploymentJob { Id = Guid.NewGuid(), Kind = JobKind.Deploy, Status = JobStatus.Queued, EnqueuedAt = DateTimeOffset.UtcNow };
                    return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(job) };
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
        public List<string> Posts { get; } = [];
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
