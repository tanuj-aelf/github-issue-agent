using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using GitHubIssueAnalysis.GAgents;
using Serilog;
using Serilog.Events;
using OrleansDashboard;
using Orleans.EventSourcing;

// Configure logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((hostContext, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
        config.AddUserSecrets<Program>(optional: true);
        config.AddCommandLine(args);
    })
    .UseSerilog()
    .UseOrleans(silo =>
    {
        silo.UseLocalhostClustering()
            .AddMemoryGrainStorage("Default")
            .AddMemoryGrainStorage("PubSubStore")
            .AddMemoryGrainStorage("EventStorage")
            .AddMemoryStreams("MemoryStreams")
            .AddLogStorageBasedLogConsistencyProvider()
            .AddStateStorageBasedLogConsistencyProvider()
            .UseDashboard(options => 
            {
                options.Port = 8888;
            })
            .ConfigureLogging(logging => logging.AddConsole());
    })
    .ConfigureServices((hostContext, services) =>
    {
        // Register our GitHub Issue Analysis services
        services.AddGitHubIssueAnalysisGAgents(hostContext.Configuration);
    })
    .UseConsoleLifetime();

// Build and run the host
using var host = builder.Build();
await host.RunAsync(); 