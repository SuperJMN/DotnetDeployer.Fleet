using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.Core.Domain;
using DotnetDeployer.Fleet.Tooling.Agents;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FleetOperationsTests
{
    private static readonly string HeadSha = new('b', 40);
    private readonly FakeCoordinator coordinator = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

    private FleetOperations Operations(Action? onEachPoll = null) =>
        new(coordinator.CreateClient(), clock, (span, _) =>
        {
            clock.Advance(span);
            onEachPoll?.Invoke();
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Deploy_queues_the_branch_head_when_no_job_exists_for_it()
    {
        var project = coordinator.AddProject("App");
        coordinator.Heads[project.Id] = HeadSha;

        var result = await Operations().DeployAsync("app", null, force: false);

        result.Outcome.Should().Be(DeployResult.Queued);
        result.CommitSha.Should().Be(HeadSha);
        coordinator.Jobs.Should().ContainSingle(j => j.TriggerCommitSha == HeadSha && j.Id == result.Job.Id);
    }

    [Fact]
    public async Task Deploy_reports_a_finished_deploy_of_the_same_commit_instead_of_queueing_another()
    {
        var project = coordinator.AddProject("App");
        coordinator.Heads[project.Id] = HeadSha;
        var previous = coordinator.AddJob(project, JobStatus.Succeeded, HeadSha);

        var result = await Operations().DeployAsync("App", null, force: false);

        result.Outcome.Should().Be(DeployResult.AlreadyDeployed);
        result.Job.Id.Should().Be(previous.Id);
        coordinator.Jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task Deploy_reports_an_active_deploy_of_the_same_commit()
    {
        var project = coordinator.AddProject("App");
        var running = coordinator.AddJob(project, JobStatus.Running, HeadSha);

        var result = await Operations().DeployAsync("App", HeadSha, force: false);

        result.Outcome.Should().Be(DeployResult.AlreadyActive);
        result.Job.Id.Should().Be(running.Id);
        coordinator.Jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task Forced_deploy_queues_a_new_job_even_when_the_commit_was_deployed()
    {
        var project = coordinator.AddProject("App");
        coordinator.AddJob(project, JobStatus.Running, HeadSha);

        var result = await Operations().DeployAsync("App", HeadSha, force: true);

        result.Outcome.Should().Be(DeployResult.Queued);
        result.ExistingJobs.Should().ContainSingle();
        coordinator.Jobs.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(JobStatus.Succeeded, FleetExitCode.Success)]
    [InlineData(JobStatus.Failed, FleetExitCode.JobFailed)]
    [InlineData(JobStatus.Cancelled, FleetExitCode.Cancelled)]
    public async Task Wait_returns_the_exit_code_of_the_final_status(JobStatus final, int expectedExitCode)
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Queued);
        var statuses = new Queue<JobStatus>([JobStatus.Running, final]);
        var progress = new List<JobStatus>();

        var result = await Operations(() => job.Status = statuses.Dequeue())
            .WaitAsync(job.Id.ToString(), WaitUntil.Finished, TimeSpan.FromHours(1), view => progress.Add(view.Status));

        result.ExitCode.Should().Be(expectedExitCode);
        result.TimedOut.Should().BeFalse();
        progress.Should().Equal(JobStatus.Queued, JobStatus.Running, final);
    }

    [Fact]
    public async Task Wait_times_out_with_exit_code_3_when_the_job_keeps_running()
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Running);
        var start = clock.Now;

        var result = await Operations().WaitAsync(job.Id.ToString(), WaitUntil.Finished, TimeSpan.FromMinutes(1));

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(FleetExitCode.Timeout);
        (clock.Now - start).Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Waiting_until_verified_continues_while_the_NuGet_verification_is_pending()
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Succeeded);
        job.NuGetVerification = NuGetVerificationStatus.Pending;
        var polls = 0;

        var result = await Operations(() =>
            {
                if (++polls == 3) job.NuGetVerification = NuGetVerificationStatus.Verified;
            })
            .WaitAsync(job.Id.ToString(), WaitUntil.Verified, null);

        polls.Should().Be(3);
        result.Job.NuGetVerification.Should().Be(NuGetVerificationStatus.Verified);
        result.ExitCode.Should().Be(FleetExitCode.Success);
    }

    [Fact]
    public async Task Waiting_until_finished_stops_before_the_NuGet_verification()
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Succeeded);
        job.NuGetVerification = NuGetVerificationStatus.Pending;

        var result = await Operations().WaitAsync(job.Id.ToString("N")[..8], WaitUntil.Finished, null);

        result.Job.NuGetVerification.Should().Be(NuGetVerificationStatus.Pending);
        result.ExitCode.Should().Be(FleetExitCode.Success);
    }

    [Fact]
    public async Task Logs_apply_grep_before_tail_and_end_while_the_job_is_running()
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Running,
            null, "restore", "error CS0246: first", "build", "error CS1002: second", "error CS0103: third", "pack");

        var logs = await Operations().GetLogsAsync(job.Id.ToString(), tail: 2, since: null, grep: "^error");

        logs.Lines.Select(l => l.Text).Should().Equal("error CS1002: second", "error CS0103: third");
        logs.Lines.Select(l => l.Number).Should().Equal(4, 5);
        logs.Status.Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task Ensure_project_creates_then_reports_unchanged_then_updates_only_the_differences()
    {
        var ops = Operations();

        var created = await ops.EnsureProjectAsync("Lib", "https://github.com/acme/lib.git", "master", 5, true, ["Acme.Lib"]);
        var unchanged = await ops.EnsureProjectAsync("Lib", "https://github.com/acme/lib.git", "master", 5, true, ["acme.lib"]);
        var updated = await ops.EnsureProjectAsync("Lib", null, null, null, false, null);

        created.Outcome.Should().Be("Created");
        unchanged.Outcome.Should().Be("Unchanged");
        updated.Outcome.Should().Be("Updated");
        updated.Changes.Should().ContainSingle().Which.Should().StartWith("runTestsBeforeDeploy");
        coordinator.Projects.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Name = "Lib",
            PollingIntervalMinutes = 5,
            RunTestsBeforeDeploy = false,
            ExpectedPackageIds = new[] { "Acme.Lib" }
        });
    }

    [Fact]
    public async Task Ensure_project_cannot_create_without_a_git_url()
    {
        var act = () => Operations().EnsureProjectAsync("Lib", null, null, null, null, null);

        await act.Should().ThrowAsync<FleetUsageException>().WithMessage("*--git-url*");
    }

    [Fact]
    public async Task Secret_listings_and_results_never_contain_secret_values()
    {
        var project = coordinator.AddProject("App");
        coordinator.Secrets.Add(new Secret { Name = "NUGET_API_KEY", Value = "global-value-123" });
        coordinator.Secrets.Add(new Secret { Name = "SIGNING_PASS", Value = "project-value-456", ProjectId = project.Id });
        var ops = Operations();

        var secrets = await ops.ListSecretsAsync("App");
        var detail = await ops.ShowProjectAsync("App");
        var set = await ops.SetSecretAsync("GITHUB_TOKEN", "App", "new-value-789", overwrite: false);

        secrets.Select(s => (s.Name, s.Scope)).Should().Equal(("NUGET_API_KEY", "global"), ("SIGNING_PASS", "App"));
        var json = AgentOutput.Serialize(new object[] { secrets, detail, set });
        json.Should().NotContain("global-value-123").And.NotContain("project-value-456").And.NotContain("new-value-789");
    }

    [Fact]
    public async Task Replacing_an_existing_secret_requires_confirmation()
    {
        coordinator.Secrets.Add(new Secret { Name = "NUGET_API_KEY", Value = "old" });
        var ops = Operations();

        var refused = () => ops.SetSecretAsync("NUGET_API_KEY", null, "new", overwrite: false);
        await refused.Should().ThrowAsync<FleetUsageException>().WithMessage("*--yes*");
        coordinator.Secrets.Single().Value.Should().Be("old");

        var result = await ops.SetSecretAsync("NUGET_API_KEY", null, "new", overwrite: true);
        result.Outcome.Should().Be("Updated");
        coordinator.Secrets.Single().Value.Should().Be("new");
    }

    [Fact]
    public async Task Workers_show_their_current_job_and_flag_silent_ones_as_disconnected()
    {
        var project = coordinator.AddProject("App");
        var busyId = Guid.NewGuid();
        coordinator.Workers.Add(new FleetApiClient.WorkerInfo(busyId, "Peach", WorkerStatus.Busy, false, clock.Now.AddSeconds(-20), 10, null,
            Environment: new WorkerEnvironment { AndroidSdkPath = null, JavaHome = "/usr/lib/jvm/java-17" }));
        coordinator.Workers.Add(new FleetApiClient.WorkerInfo(Guid.NewGuid(), "rpi4", WorkerStatus.Online, false, clock.Now.AddMinutes(-30), 10, null));
        var job = coordinator.AddJob(project, JobStatus.Running);
        job.WorkerId = busyId;

        var workers = await Operations().ListWorkersAsync();

        workers.Should().SatisfyRespectively(
            peach =>
            {
                peach.Disconnected.Should().BeFalse();
                peach.CurrentJobId.Should().Be(job.Id);
                peach.CurrentJobProject.Should().Be("App");
                peach.Environment!.HasAndroidToolchain.Should().BeFalse();
            },
            rpi => rpi.Disconnected.Should().BeTrue());
    }

    [Fact]
    public async Task Jobs_list_includes_worker_name_and_filters_by_project_and_status()
    {
        var app = coordinator.AddProject("App");
        var other = coordinator.AddProject("Other", "https://github.com/acme/other.git");
        var workerId = Guid.NewGuid();
        coordinator.Workers.Add(new FleetApiClient.WorkerInfo(workerId, "Peach", WorkerStatus.Busy, false, clock.Now, 10, null));
        var failed = coordinator.AddJob(app, JobStatus.Failed);
        failed.WorkerId = workerId;
        coordinator.AddJob(app, JobStatus.Succeeded);
        coordinator.AddJob(other, JobStatus.Failed);

        var jobs = await Operations().ListJobsAsync("https://github.com/acme/app", JobStatus.Failed, 10);

        jobs.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = failed.Id,
            ProjectName = "App",
            WorkerName = "Peach",
            Status = JobStatus.Failed
        });
    }

    [Fact]
    public async Task Failure_analysis_combines_phases_and_logs_of_the_job()
    {
        var project = coordinator.AddProject("App");
        var job = coordinator.AddJob(project, JobStatus.Failed, null,
            "/src/App/Program.cs(3,1): error CS0246: The type or namespace name 'Foo' could not be found [/src/App/App.csproj]");
        coordinator.Phases.Add(new JobPhase { JobId = job.Id, Name = "worker.solution.build", Status = PhaseStatus.Fail });

        var report = await Operations().AnalyzeFailureAsync(job.Id.ToString());

        report.FailedPhase.Should().Be("worker.solution.build");
        report.Origin.Should().Be(FailureOrigin.Code);
        report.BuildErrors.Should().ContainSingle(e => e.Code == "CS0246" && e.Project == "App.csproj");
    }
}
