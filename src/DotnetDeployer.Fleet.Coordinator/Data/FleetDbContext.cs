using DotnetDeployer.Fleet.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DotnetDeployer.Fleet.Coordinator.Data;

public class FleetDbContext : DbContext
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DeploymentJob> DeploymentJobs => Set<DeploymentJob>();
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<RepoCache> RepoCaches => Set<RepoCache>();
    public DbSet<LogEntry> LogEntries => Set<LogEntry>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<JobDurationStat> JobDurationStats => Set<JobDurationStat>();
    public DbSet<JobPhase> JobPhases => Set<JobPhase>();

    public FleetDbContext(DbContextOptions<FleetDbContext> options) : base(options) { }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY DateTimeOffset natively — store as long (binary ticks)
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Name);
            e.Property(p => p.RunTestsBeforeDeploy)
                .HasDefaultValue(true)
                .HasSentinel(true);
        });

        modelBuilder.Entity<DeploymentJob>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => j.ProjectId);
            e.HasIndex(j => j.Kind);
            e.HasIndex(j => j.Status);
            e.HasIndex(j => j.EnqueuedAt);
        });

        modelBuilder.Entity<Worker>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.Name).IsUnique();
        });

        modelBuilder.Entity<RepoCache>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.WorkerId, r.ProjectId }).IsUnique();
        });

        modelBuilder.Entity<LogEntry>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.JobId);
            e.HasIndex(l => l.Timestamp);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<Secret>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.ProjectId);
            e.HasIndex(s => new { s.ProjectId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<JobDurationStat>(e =>
        {
            e.HasKey(s => new { s.ProjectId, s.WorkerId });
            e.HasIndex(s => s.WorkerId);
        });

        modelBuilder.Entity<JobPhase>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.JobId);
            e.HasIndex(p => new { p.JobId, p.StartedAt });
        });
    }
}
