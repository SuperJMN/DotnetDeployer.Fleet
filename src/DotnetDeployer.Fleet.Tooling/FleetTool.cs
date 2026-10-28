using System.Reflection;
using System.CommandLine;
using DotnetDeployer.Fleet.Coordinator;
using DotnetDeployer.Fleet.Tooling.Agents;
using DotnetDeployer.Fleet.WorkerService;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DotnetDeployer.Fleet.Tooling;

public static class FleetTool
{
    public static int Run(string[] args)
    {
        var rootCommand = new RootCommand($"{ToolIdentity.ProductName} — run DotnetDeployer on managed machines, and query or drive it as a client (also as an MCP server for AI agents)");

        // ── fleet coordinator ────────────────────────────────────────────────────────

        var coordinatorCommand = new Command("coordinator", $"Start the {ToolIdentity.ProductName} coordinator (API server)");

        var portOption = new Option<int>("--port", "-p")
        {
            Description = "HTTP port to listen on",
            DefaultValueFactory = _ => 5000
        };
        var coordDataDirOption = new Option<string?>("--data-dir")
        {
            Description = "Data directory (default: ~/.fleet/coordinator)"
        };
        var tokenOption = new Option<string?>("--token", "-t")
        {
            Description = "Worker registration token (auto-generated if omitted)"
        };
        var jwtSecretOption = new Option<string?>("--jwt-secret")
        {
            Description = "JWT signing secret (auto-generated if omitted)"
        };
        var adminPasswordOption = new Option<string?>("--admin-password")
        {
            Description = "Admin user password (default: admin)"
        };
        var urlsOption = new Option<string?>("--urls")
        {
            Description = "ASP.NET Core URLs override (e.g. http://0.0.0.0:5000)"
        };
        var noMdnsOption = new Option<bool>("--no-mdns")
        {
            Description = "Disable mDNS advertising (workers won't auto-discover this coordinator on the LAN)"
        };

        coordinatorCommand.Options.Add(portOption);
        coordinatorCommand.Options.Add(coordDataDirOption);
        coordinatorCommand.Options.Add(tokenOption);
        coordinatorCommand.Options.Add(jwtSecretOption);
        coordinatorCommand.Options.Add(adminPasswordOption);
        coordinatorCommand.Options.Add(urlsOption);
        coordinatorCommand.Options.Add(noMdnsOption);

        // Default action: run coordinator in foreground
        coordinatorCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var port = parseResult.GetValue(portOption);
            var dataDir = parseResult.GetValue(coordDataDirOption) ?? FleetConfig.GetDefaultDataDir("coordinator");
            var jwtSecret = parseResult.GetValue(jwtSecretOption);
            var regToken = parseResult.GetValue(tokenOption);
            var adminPassword = parseResult.GetValue(adminPasswordOption);
            var urls = parseResult.GetValue(urlsOption);
            var noMdns = parseResult.GetValue(noMdnsOption);

            var config = FleetConfig.LoadOrCreateCoordinatorConfig(dataDir, jwtSecret, regToken, port);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            PrintCoordinatorBanner(port, config.RegistrationToken, adminPassword ?? "admin", urls);

            try
            {
                var options = new CoordinatorStartupOptions
                {
                    Port = port,
                    JwtSecret = config.JwtSecret,
                    RegistrationToken = config.RegistrationToken,
                    DataDir = dataDir,
                    AdminPassword = adminPassword,
                    Urls = urls,
                    NoMdns = noMdns
                };

                var app = CoordinatorHostBuilder.Build(options, []);
                await CoordinatorHostBuilder.InitializeDatabaseAsync(app);
                await HostingAbstractionsHostExtensions.RunAsync(app, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Fatal(ex, "Coordinator terminated unexpectedly");
                return 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }

            return 0;
        });

        // ── fleet coordinator install ────────────────────────────────────────────
        var coordInstallCommand = new Command("install", "Install coordinator as an OS service");
        coordInstallCommand.Options.Add(portOption);
        coordInstallCommand.Options.Add(coordDataDirOption);
        coordInstallCommand.Options.Add(tokenOption);
        coordInstallCommand.Options.Add(jwtSecretOption);
        coordInstallCommand.Options.Add(adminPasswordOption);
        coordInstallCommand.Options.Add(urlsOption);
        coordInstallCommand.Options.Add(noMdnsOption);

        coordInstallCommand.SetAction(async (parseResult, _) =>
        {
            try
            {
                var elevated = SudoElevation.ReExecAsRootIfNeeded();
                if (elevated.HasValue) return elevated.Value;

                var port = parseResult.GetValue(portOption);
                var dataDir = parseResult.GetValue(coordDataDirOption) ?? ServiceInstaller.GetDefaultInstallDataDir("coordinator");
                await ServiceInstaller.InstallCoordinatorAsync(new ServiceInstaller.CoordinatorInstallOptions(
                    Port: port,
                    DataDir: dataDir,
                    Token: parseResult.GetValue(tokenOption),
                    JwtSecret: parseResult.GetValue(jwtSecretOption),
                    AdminPassword: parseResult.GetValue(adminPasswordOption),
                    Urls: parseResult.GetValue(urlsOption),
                    NoMdns: parseResult.GetValue(noMdnsOption)));
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 1;
            }
        });

        // ── fleet coordinator uninstall ──────────────────────────────────────────
        var coordUninstallCommand = new Command("uninstall", "Remove the coordinator OS service");
        coordUninstallCommand.SetAction(async (_, _) =>
        {
            try
            {
                var elevated = SudoElevation.ReExecAsRootIfNeeded();
                if (elevated.HasValue) return elevated.Value;

                await ServiceInstaller.UninstallCoordinatorAsync();
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 1;
            }
        });

        // ── fleet coordinator status ─────────────────────────────────────────────
        var coordStatusCommand = new Command("status", "Show coordinator service status");
        coordStatusCommand.SetAction(async (_, _) =>
        {
            await ServiceInstaller.CoordinatorStatusAsync();
        });

        coordinatorCommand.Subcommands.Add(coordInstallCommand);
        coordinatorCommand.Subcommands.Add(coordUninstallCommand);
        coordinatorCommand.Subcommands.Add(coordStatusCommand);

        // ── fleet worker ─────────────────────────────────────────────────────────────

        var workerCommand = new Command("worker", $"Start a {ToolIdentity.ProductName} worker that connects to a coordinator");

        var coordinatorUrlOption = new Option<string?>("--coordinator", "-c")
        {
            Description = "Coordinator URL (e.g. http://myserver:5000). Auto-discovered on the local machine and via mDNS on the LAN if omitted."
        };
        var workerTokenOption = new Option<string?>("--token", "-t")
        {
            Description = "Registration token (required on first run; auto-discovered when the coordinator runs on this machine)"
        };
        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "Worker display name (default: hostname)"
        };
        var workerDataDirOption = new Option<string?>("--data-dir")
        {
            Description = "Data directory (default: ~/.fleet/worker-{name})"
        };
        var pollIntervalOption = new Option<int?>("--poll-interval")
        {
            Description = "Queue polling interval in seconds (default: 10)"
        };
        var maxDiskOption = new Option<double?>("--max-disk")
        {
            Description = "Max disk usage in GB for repo cache (default: 10)"
        };
        var noDiscoverOption = new Option<bool>("--no-discover")
        {
            Description = "Disable auto-discovery (local + mDNS) of the coordinator"
        };

        workerCommand.Options.Add(coordinatorUrlOption);
        workerCommand.Options.Add(workerTokenOption);
        workerCommand.Options.Add(nameOption);
        workerCommand.Options.Add(workerDataDirOption);
        workerCommand.Options.Add(pollIntervalOption);
        workerCommand.Options.Add(maxDiskOption);
        workerCommand.Options.Add(noDiscoverOption);

        // Default action: run worker in foreground
        workerCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var explicitUrl = parseResult.GetValue(coordinatorUrlOption);
            var explicitToken = parseResult.GetValue(workerTokenOption);
            var noDiscover = parseResult.GetValue(noDiscoverOption);
            var workerName = parseResult.GetValue(nameOption) ?? Environment.MachineName;
            var dataDir = parseResult.GetValue(workerDataDirOption)
                          ?? FleetConfig.GetDefaultDataDir($"worker-{workerName}");
            var pollInterval = parseResult.GetValue(pollIntervalOption);
            var maxDisk = parseResult.GetValue(maxDiskOption);

            var resolved = await CoordinatorResolver.ResolveAsync(explicitUrl, explicitToken, noDiscover);
            if (resolved == null) return 1;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Console.WriteLine();
            Console.WriteLine($"  {ToolIdentity.ProductName} Worker \"{workerName}\"");
            Console.WriteLine($"  Coordinator: {resolved.Url}");
            Console.WriteLine($"  Data dir:    {dataDir}");
            Console.WriteLine();

            try
            {
                var options = new WorkerStartupOptions
                {
                    CoordinatorUrl = resolved.Url,
                    RegistrationToken = resolved.Token,
                    Name = workerName,
                    DataDir = dataDir,
                    PollIntervalSeconds = pollInterval,
                    MaxDiskGb = maxDisk
                };

                var host = await WorkerHostBuilder.BuildAsync(options, []);
                await host.RunAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Fatal(ex, "Worker terminated unexpectedly");
                return 1;
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }

            return 0;
        });

        // ── fleet worker install ─────────────────────────────────────────────────
        var workerInstallCommand = new Command("install", "Install worker as an OS service");
        workerInstallCommand.Options.Add(coordinatorUrlOption);
        workerInstallCommand.Options.Add(workerTokenOption);
        workerInstallCommand.Options.Add(nameOption);
        workerInstallCommand.Options.Add(workerDataDirOption);
        workerInstallCommand.Options.Add(pollIntervalOption);
        workerInstallCommand.Options.Add(maxDiskOption);
        workerInstallCommand.Options.Add(noDiscoverOption);

        workerInstallCommand.SetAction(async (parseResult, _) =>
        {
            try
            {
                var elevated = SudoElevation.ReExecAsRootIfNeeded();
                if (elevated.HasValue) return elevated.Value;

                var workerName = parseResult.GetValue(nameOption) ?? Environment.MachineName;
                var dataDir = parseResult.GetValue(workerDataDirOption)
                              ?? ServiceInstaller.GetDefaultInstallDataDir($"worker-{workerName}");
                var explicitUrl = parseResult.GetValue(coordinatorUrlOption);
                var explicitToken = parseResult.GetValue(workerTokenOption);
                var noDiscover = parseResult.GetValue(noDiscoverOption);

                var resolved = await CoordinatorResolver.ResolveAsync(explicitUrl, explicitToken, noDiscover);
                if (resolved == null) return 1;

                await ServiceInstaller.InstallWorkerAsync(new ServiceInstaller.WorkerInstallOptions(
                    CoordinatorUrl: resolved.Url,
                    Token: resolved.Token,
                    Name: workerName,
                    DataDir: dataDir,
                    PollInterval: parseResult.GetValue(pollIntervalOption),
                    MaxDisk: parseResult.GetValue(maxDiskOption),
                    NoDiscover: noDiscover));
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 1;
            }
        });

        // ── fleet worker uninstall ───────────────────────────────────────────────
        var workerUninstallCommand = new Command("uninstall", "Remove a worker OS service");
        var uninstallNameOption = new Option<string?>("--name", "-n")
        {
            Description = "Worker name to uninstall (default: hostname)"
        };
        workerUninstallCommand.Options.Add(uninstallNameOption);
        workerUninstallCommand.SetAction(async (parseResult, _) =>
        {
            try
            {
                var elevated = SudoElevation.ReExecAsRootIfNeeded();
                if (elevated.HasValue) return elevated.Value;

                var workerName = parseResult.GetValue(uninstallNameOption) ?? Environment.MachineName;
                await ServiceInstaller.UninstallWorkerAsync(workerName);
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 1;
            }
        });

        // ── fleet worker status ──────────────────────────────────────────────────
        var workerStatusCommand = new Command("status", "Show worker service status");
        var statusNameOption = new Option<string?>("--name", "-n")
        {
            Description = "Worker name to check (default: hostname)"
        };
        workerStatusCommand.Options.Add(statusNameOption);
        workerStatusCommand.SetAction(async (parseResult, _) =>
        {
            var workerName = parseResult.GetValue(statusNameOption) ?? Environment.MachineName;
            await ServiceInstaller.WorkerStatusAsync(workerName);
        });

        var workerUnregisterCommand = WorkerUnregisterCli.Create(coordinatorUrlOption, noDiscoverOption);

        workerCommand.Subcommands.Add(workerInstallCommand);
        workerCommand.Subcommands.Add(workerUninstallCommand);
        workerCommand.Subcommands.Add(workerStatusCommand);
        workerCommand.Subcommands.Add(workerUnregisterCommand);

        // ── fleet update ─────────────────────────────────────────────────────────────

        var updateCommand = new Command("update", $"Update {ToolIdentity.CurrentPackageId} and restart local coordinator/worker services");

        var skipToolUpdateOption = new Option<bool>("--skip-tool-update")
        {
            Description = "Only restart installed services; skip 'dotnet tool update'"
        };
        var updateVersionOption = new Option<string?>("--version")
        {
            Description = $"Pin a specific {ToolIdentity.CurrentPackageId} version (default: latest)"
        };
        var updatePrereleaseOption = new Option<bool>("--prerelease")
        {
            Description = "Allow prerelease versions when updating the tool"
        };

        updateCommand.Options.Add(skipToolUpdateOption);
        updateCommand.Options.Add(updateVersionOption);
        updateCommand.Options.Add(updatePrereleaseOption);

        updateCommand.SetAction(async (parseResult, _) =>
        {
            var elevated = SudoElevation.ReExecAsRootIfNeeded();
            if (elevated.HasValue) return elevated.Value;

            try
            {
                await ServiceInstaller.UpdateLocalServicesAsync(new ServiceInstaller.UpdateOptions(
                    SkipToolUpdate: parseResult.GetValue(skipToolUpdateOption),
                    Version: parseResult.GetValue(updateVersionOption),
                    IncludePrerelease: parseResult.GetValue(updatePrereleaseOption)));
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        });

        // ── fleet version ────────────────────────────────────────────────────────────

        var versionCommand = new Command("version", $"Show {ToolIdentity.ProductName} version");
        versionCommand.SetAction(_ =>
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                          ?? typeof(FleetConfig).Assembly.GetName().Version?.ToString(3)
                          ?? "0.0.0";
            Console.WriteLine($"{ToolIdentity.ProductName} {version}");
        });

        // ── Root ─────────────────────────────────────────────────────────────────────

        rootCommand.Subcommands.Add(coordinatorCommand);
        rootCommand.Subcommands.Add(workerCommand);
        rootCommand.Subcommands.Add(updateCommand);
        rootCommand.Subcommands.Add(versionCommand);

        // ── Client commands for people and agents (projects, jobs, logs, …) and fleet mcp ──
        AgentCli.AddTo(rootCommand);

        var parseResult = rootCommand.Parse(args);
        if (parseResult.Errors.Count > 0 && AgentCli.ClientCommands.Contains(parseResult.CommandResult.Command))
        {
            foreach (var error in parseResult.Errors)
                Console.Error.WriteLine($"Error: {error.Message}");
            Console.Error.WriteLine($"Run 'fleet {string.Join(' ', CommandPath(parseResult))} --help' for usage.");
            return FleetExitCode.UsageOrAuth;
        }

        return parseResult.Invoke();
    }

    private static IEnumerable<string> CommandPath(ParseResult parseResult)
    {
        var names = new Stack<string>();
        for (var result = parseResult.CommandResult; result.Parent is System.CommandLine.Parsing.CommandResult parent; result = parent)
            names.Push(result.Command.Name);
        return names;
    }

    private static void PrintCoordinatorBanner(int port, string registrationToken, string adminPassword, string? urls)
    {
        var listenUrl = urls ?? $"http://0.0.0.0:{port}";

        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("  ║              DotnetDeployer.Fleet Coordinator                ║");
        Console.WriteLine("  ╠══════════════════════════════════════════════════════════════╣");
        Console.WriteLine($"  ║  Listening on:       {Truncate(listenUrl, 37),-37} ║");
        Console.WriteLine($"  ║  Admin credentials:  admin / {Truncate(adminPassword, 28),-28} ║");
        Console.WriteLine($"  ║  Registration token: {Truncate(registrationToken, 37),-37} ║");
        Console.WriteLine("  ╠══════════════════════════════════════════════════════════════╣");
        Console.WriteLine("  ║  Connect workers with:                                      ║");
        Console.WriteLine($"  ║    fleet worker --coordinator {Truncate(listenUrl, 28),-28} \\  ║");
        Console.WriteLine($"  ║                --token {Truncate(registrationToken, 34),-34} \\  ║");
        Console.WriteLine("  ╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
