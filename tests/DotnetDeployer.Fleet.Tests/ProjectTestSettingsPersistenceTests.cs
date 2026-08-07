using DotnetDeployer.Fleet.Coordinator;
using DotnetDeployer.Fleet.Coordinator.Data;
using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ProjectTestSettingsPersistenceTests : IDisposable
{
    private readonly string dbPath = Path.Combine(
        Path.GetTempPath(),
        $"fleet-project-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task New_projects_enable_solution_tests_by_default_and_allow_opt_out()
    {
        var options = CreateOptions();
        await using (var db = new FleetDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Projects.AddRange(
                new Project { Name = "default", GitUrl = "https://example.com/default.git" },
                new Project
                {
                    Name = "opt-out",
                    GitUrl = "https://example.com/opt-out.git",
                    RunTestsBeforeDeploy = false
                });
            await db.SaveChangesAsync();
        }

        await using var verify = new FleetDbContext(options);
        var projects = await verify.Projects.OrderBy(project => project.Name).ToListAsync();

        projects.Single(project => project.Name == "default").RunTestsBeforeDeploy.Should().BeTrue();
        projects.Single(project => project.Name == "opt-out").RunTestsBeforeDeploy.Should().BeFalse();

        projects.Single(project => project.Name == "default").RunTestsBeforeDeploy = false;
        projects.Single(project => project.Name == "opt-out").RunTestsBeforeDeploy = true;
        await verify.SaveChangesAsync();
        verify.ChangeTracker.Clear();

        var updated = await verify.Projects.OrderBy(project => project.Name).ToListAsync();
        updated.Single(project => project.Name == "default").RunTestsBeforeDeploy.Should().BeFalse();
        updated.Single(project => project.Name == "opt-out").RunTestsBeforeDeploy.Should().BeTrue();
    }

    [Fact]
    public async Task Existing_database_projects_are_migrated_with_tests_enabled()
    {
        var options = CreateOptions();
        await using var db = new FleetDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "Projects" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "GitUrl" TEXT NOT NULL,
                "Branch" TEXT NOT NULL,
                "PollingIntervalMinutes" INTEGER NOT NULL,
                "CreatedAt" INTEGER NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Projects"
                ("Id", "Name", "GitUrl", "Branch", "PollingIntervalMinutes", "CreatedAt")
            VALUES
                ('00000000-0000-0000-0000-000000000001', 'legacy', 'https://example.com/legacy.git', 'main', 0, 0)
            """);

        await CoordinatorHostBuilder.EnsureRunTestsBeforeDeployColumnAsync(db);

        var values = await db.Database
            .SqlQueryRaw<long>("SELECT \"RunTestsBeforeDeploy\" AS \"Value\" FROM \"Projects\"")
            .ToListAsync();
        var defaults = await db.Database
            .SqlQueryRaw<string>("SELECT dflt_value AS \"Value\" FROM pragma_table_info('Projects') WHERE name='RunTestsBeforeDeploy'")
            .ToListAsync();

        values.Should().Equal(1);
        defaults.Should().Equal("1");
    }

    private DbContextOptions<FleetDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

    public void Dispose()
    {
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }
}
