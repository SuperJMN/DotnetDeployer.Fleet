using DotnetDeployer.Fleet.Core.Interfaces;
using DotnetDeployer.Fleet.WorkerService.Bootstrap;
using DotnetDeployer.Fleet.WorkerService.Coordinator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace DotnetDeployer.Fleet.WorkerService;

public class WorkerStartupOptions
{
    public string? CoordinatorUrl { get; set; }
    public string? RegistrationToken { get; set; }
    public string? Name { get; set; }
    public string? DataDir { get; set; }
    public int? PollIntervalSeconds { get; set; }
    public double? MaxDiskGb { get; set; }
}

public static class WorkerHostBuilder
{
    private const string TokenHttpClient = "WorkerTokenManager";

    public static async Task<IHost> BuildAsync(WorkerStartupOptions? options, string[] args)
    {
        options ??= new WorkerStartupOptions();

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(serviceOptions =>
        {
            serviceOptions.ServiceName = "DotnetDeployer.Fleet Worker";
        });

        builder.Configuration.AddEnvironmentVariables(prefix: "FLEET_");

        // Apply CLI overrides
        ApplyOverrides(builder.Configuration, options);

        var logPath = ResolveLogPath(options.DataDir);
        var logDir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(logDir))
            Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);

        var workerSection = builder.Configuration.GetSection(WorkerOptions.SectionName);
        builder.Services.Configure<WorkerOptions>(workerSection);

        var bootOptions = workerSection.Get<WorkerOptions>() ?? new WorkerOptions();

        // If a data dir was specified, adjust credentials file and repo storage path
        if (!string.IsNullOrEmpty(options.DataDir))
        {
            Directory.CreateDirectory(options.DataDir);
            bootOptions.CredentialsFile = Path.Combine(options.DataDir, "worker.json");
            if (string.IsNullOrEmpty(options.CoordinatorUrl))
                bootOptions.RepoStoragePath = Path.Combine(options.DataDir, "fleet-repos");
        }

        using (var bootLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger)))
        {
            var bootLogger = bootLoggerFactory.CreateLogger("Bootstrap");
            await WorkerBootstrap.EnsureCredentialsAsync(bootOptions, bootLogger);
        }

        builder.Services.PostConfigure<WorkerOptions>(o =>
        {
            o.Id = bootOptions.Id;
            o.Secret = bootOptions.Secret;
            if (!string.IsNullOrEmpty(options.DataDir))
            {
                o.CredentialsFile = bootOptions.CredentialsFile;
                o.RepoStoragePath = bootOptions.RepoStoragePath;
            }
        });

        builder.Services.AddTransient<WorkerAuthHandler>();

        builder.Services.AddHttpClient(TokenHttpClient, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
            client.BaseAddress = new Uri(opts.CoordinatorBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
        });

        builder.Services.AddSingleton<WorkerTokenManager>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new WorkerTokenManager(
                factory.CreateClient(TokenHttpClient),
                sp.GetRequiredService<IOptions<WorkerOptions>>(),
                sp.GetRequiredService<ILogger<WorkerTokenManager>>());
        });

        builder.Services
            .AddHttpClient<IWorkerCoordinatorClient, HttpWorkerCoordinatorClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                client.BaseAddress = new Uri(opts.CoordinatorBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
            })
            .AddHttpMessageHandler<WorkerAuthHandler>();

        builder.Services
            .AddHttpClient<WorkerTransferClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                client.BaseAddress = new Uri(opts.CoordinatorBaseUrl);
                client.Timeout = TimeSpan.FromMinutes(opts.TransferTimeoutMinutes);
            })
            .AddHttpMessageHandler<WorkerAuthHandler>();

        builder.Services
            .AddHttpClient<IWorkerJobSource, RemoteWorkerJobSource>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
                client.BaseAddress = new Uri(opts.CoordinatorBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
            })
            .AddHttpMessageHandler<WorkerAuthHandler>();

        builder.Services.AddHostedService<RemoteWorkerBackgroundService>();

        return builder.Build();
    }

    private static void ApplyOverrides(ConfigurationManager config, WorkerStartupOptions options)
    {
        var overrides = new Dictionary<string, string?>();

        if (!string.IsNullOrEmpty(options.CoordinatorUrl))
            overrides["Worker:CoordinatorBaseUrl"] = options.CoordinatorUrl;

        if (!string.IsNullOrEmpty(options.RegistrationToken))
            overrides["Worker:RegistrationToken"] = options.RegistrationToken;

        if (!string.IsNullOrEmpty(options.Name))
            overrides["Worker:Name"] = options.Name;

        if (options.PollIntervalSeconds.HasValue)
            overrides["Worker:PollIntervalSeconds"] = options.PollIntervalSeconds.Value.ToString();

        if (options.MaxDiskGb.HasValue)
            overrides["Worker:MaxDiskUsageBytes"] = ((long)(options.MaxDiskGb.Value * 1024 * 1024 * 1024)).ToString();

        if (!string.IsNullOrEmpty(options.DataDir))
        {
            overrides["Worker:CredentialsFile"] = Path.Combine(options.DataDir, "worker.json");
            overrides["Worker:RepoStoragePath"] = Path.Combine(options.DataDir, "fleet-repos");
        }

        if (overrides.Count > 0)
            config.AddInMemoryCollection(overrides);
    }

    internal static string ResolveLogPath(string? dataDir)
    {
        return string.IsNullOrEmpty(dataDir)
            ? Path.Combine("logs", "worker-.log")
            : Path.Combine(dataDir, "logs", "worker-.log");
    }
}
