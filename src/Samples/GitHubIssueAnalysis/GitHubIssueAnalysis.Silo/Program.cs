using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using GitHubIssueAnalysis.GAgents;
using Serilog;
using Serilog.Events;
using OrleansDashboard;
using Orleans.EventSourcing;
using System.IO;

// Load .env file if it exists
string basePath = AppDomain.CurrentDomain.BaseDirectory;
string envFile = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(envFile))
{
    Console.WriteLine($"Loading environment variables from: {envFile}");
    foreach (var line in File.ReadAllLines(envFile))
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
            continue;
            
        var parts = line.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim();
            var value = parts[1].Trim();
            Environment.SetEnvironmentVariable(key, value);
            Console.WriteLine($"Loaded environment variable: {key}");
        }
    }
}

// Configure logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Orleans", LogEventLevel.Warning)
    .MinimumLevel.Override("Orleans.Runtime", LogEventLevel.Warning)
    .MinimumLevel.Override("Orleans.Providers", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
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
            .AddMemoryStreams("Aevatar")
            .AddLogStorageBasedLogConsistencyProvider()
            .AddStateStorageBasedLogConsistencyProvider()
            .UseDashboard(options => 
            {
                options.Port = 8888;
            })
            .ConfigureLogging(logging => 
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug);
            });
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