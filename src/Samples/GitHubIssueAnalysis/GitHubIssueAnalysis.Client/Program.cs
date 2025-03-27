using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using GitHubIssueAnalysis.GAgents.Common;
using GitHubIssueAnalysis.GAgents.GitHubAnalysis;
using Orleans.Runtime;
using Orleans.Streams;
using Serilog;
using Serilog.Events;
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
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

// Configure and build the host
var host = Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration((hostContext, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true);
        config.AddEnvironmentVariables();
        config.AddUserSecrets<Program>(optional: true);
    })
    .UseSerilog()
    .UseOrleansClient(clientBuilder => 
    {
        clientBuilder.UseLocalhostClustering();
        
        // Configure the memory streams provider
        clientBuilder.AddMemoryStreams("MemoryStreams");
        
        // Add the Aevatar stream provider to match the Silo configuration
        clientBuilder.AddMemoryStreams("Aevatar");
    })
    .ConfigureServices((hostContext, services) => 
    {
        // Register our GitHub client with configuration
        services.AddTransient<GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient>(provider => 
        {
            // Use configuration for PAT if available, or fallback to environment variable
            var configuration = provider.GetRequiredService<IConfiguration>();
            string personalAccessToken = configuration["GitHub:PersonalAccessToken"] ?? "";
            
            // Check if empty and try environment variable
            if (string.IsNullOrEmpty(personalAccessToken))
            {
                personalAccessToken = Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN") ?? "";
                Console.WriteLine("Using GitHub token from environment variable");
            }
            
            if (string.IsNullOrEmpty(personalAccessToken))
            {
                Console.WriteLine("WARNING: No GitHub Personal Access Token found. Some GitHub API calls may fail.");
            }
            
            return new GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient(personalAccessToken: personalAccessToken);
        });
    })
    .Build();

// Start the host
await host.StartAsync();

// Get the client and cluster client
var gitHubClient = host.Services.GetRequiredService<GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient>();
var clusterClient = host.Services.GetRequiredService<IClusterClient>();

Console.WriteLine("===============================================");
Console.WriteLine("GitHub Issue Analysis Sample Client");
Console.WriteLine("===============================================");

bool exitRequested = false;

while (!exitRequested)
{
    Console.WriteLine("\nPlease choose an option:");
    Console.WriteLine("1) Analyze GitHub Repository Issues");
    Console.WriteLine("2) Exit");
    Console.Write("\nYour choice: ");

    var choice = Console.ReadLine();

    switch (choice)
    {
        case "1":
            await AnalyzeGitHubRepositoryAsync(clusterClient, gitHubClient);
            break;
        case "2":
            exitRequested = true;
            break;
        default:
            Console.WriteLine("Invalid option. Please try again.");
            break;
    }
}

await host.StopAsync();

static async Task AnalyzeGitHubRepositoryAsync(
    IClusterClient clusterClient, 
    GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient gitHubClient)
{
    Console.Write("Enter GitHub repository owner: ");
    string owner = Console.ReadLine() ?? "microsoft";

    Console.Write("Enter GitHub repository name: ");
    string repo = Console.ReadLine() ?? "semantic-kernel";

    Console.Write("Enter maximum number of issues to analyze (default 10): ");
    if (!int.TryParse(Console.ReadLine(), out int maxIssues))
    {
        maxIssues = 10;
    }

    Console.WriteLine($"\nFetching up to {maxIssues} issues from {owner}/{repo}...");

    try
    {
        // Fetch issues from GitHub
        var issues = await gitHubClient.GetRepositoryIssuesAsync(owner, repo, maxIssues);
        
        Console.WriteLine($"Found {issues.Count} issues. Starting analysis...");

        if (issues.Count == 0)
        {
            Console.WriteLine("No issues found for this repository. Please try another repository or check the repository name.");
            return;
        }

        // Set up stream subscription for the event types
        var streamProvider = clusterClient.GetStreamProvider("MemoryStreams");
        
        Console.WriteLine("Setting up stream subscriptions for summary reports...");

        // Create a subscription to the static stream with empty GUID for summary reports
        var summaryStreamId = StreamId.Create("GitHubAnalysisStream", Guid.Empty);
        Console.WriteLine($"Subscribing to summary stream: GitHubAnalysisStream/{Guid.Empty}");
        
        // Subscribe to MemoryStreams first
        Console.WriteLine("Creating subscription with MemoryStreams provider...");
        var memoryStreamProvider = clusterClient.GetStreamProvider("MemoryStreams");
        var memoryStream = memoryStreamProvider.GetStream<SummaryReportEvent>(summaryStreamId);
        var memorySubscription = await memoryStream.SubscribeAsync(
            (summaryEvent, token) =>
            {
                Console.WriteLine("\n=============================================");
                Console.WriteLine($"RECEIVED SUMMARY REPORT FROM MEMORY STREAMS!");
                Console.WriteLine($"Summary Report for {summaryEvent.Repository}");
                Console.WriteLine("=============================================");
                Console.WriteLine($"Total Issues Analyzed: {summaryEvent.TotalIssuesAnalyzed}");
                Console.WriteLine($"Generated At: {summaryEvent.GeneratedAt}");
                
                Console.WriteLine("\nTag Frequencies:");
                foreach (var tag in summaryEvent.TagFrequency.OrderByDescending(t => t.Value))
                {
                    Console.WriteLine($"  - {tag.Key}: {tag.Value}");
                }
                
                Console.WriteLine("\nRecommendations:");
                foreach (var recommendation in summaryEvent.PriorityRecommendations)
                {
                    Console.WriteLine($"  - {recommendation}");
                }
                Console.WriteLine("=============================================");
                
                return Task.CompletedTask;
            });
        Console.WriteLine("Successfully created subscription to summary stream with MemoryStreams");
        
        // Also subscribe to Aevatar stream for redundancy
        Console.WriteLine("Creating subscription with Aevatar provider...");
        var aevatarProvider = clusterClient.GetStreamProvider("Aevatar");
        var aevatarStream = aevatarProvider.GetStream<SummaryReportEvent>(summaryStreamId);
        var aevatarSubscription = await aevatarStream.SubscribeAsync(
            (summaryEvent, token) =>
            {
                Console.WriteLine("\n=============================================");
                Console.WriteLine($"RECEIVED SUMMARY REPORT FROM AEVATAR STREAMS!");
                Console.WriteLine($"Summary Report for {summaryEvent.Repository}");
                Console.WriteLine("=============================================");
                Console.WriteLine($"Total Issues Analyzed: {summaryEvent.TotalIssuesAnalyzed}");
                Console.WriteLine($"Generated At: {summaryEvent.GeneratedAt}");
                
                Console.WriteLine("\nTag Frequencies:");
                foreach (var tag in summaryEvent.TagFrequency.OrderByDescending(t => t.Value))
                {
                    Console.WriteLine($"  - {tag.Key}: {tag.Value}");
                }
                
                Console.WriteLine("\nRecommendations:");
                foreach (var recommendation in summaryEvent.PriorityRecommendations)
                {
                    Console.WriteLine($"  - {recommendation}");
                }
                Console.WriteLine("=============================================");
                
                return Task.CompletedTask;
            });
        Console.WriteLine("Successfully created subscription to summary stream with Aevatar");

        try
        {
            // Get the stream for publishing issues using the correct namespace and ID
            var issuesStreamId = StreamId.Create("GitHubAnalysisStream", Guid.Parse("22222222-2222-2222-2222-222222222222"));
            Console.WriteLine($"Publishing issues to stream: GitHubAnalysisStream/22222222-2222-2222-2222-222222222222");
            var issuesStream = streamProvider.GetStream<GitHubIssueEvent>(issuesStreamId);
            
            // Also add a Aevatar stream subscription for redundancy
            var aevatarIssuesStream = aevatarProvider.GetStream<GitHubIssueEvent>(issuesStreamId);
            
            // Process each issue
            foreach (var issue in issues)
            {
                Console.WriteLine($"Publishing issue: {issue.Title}");
                
                // Create the event to send 
                var gitHubIssueEvent = new GitHubIssueEvent 
                { 
                    IssueInfo = issue 
                };
                
                // Add extra delay to allow the grain to activate and subscribe
                if (issues.IndexOf(issue) == 0)
                {
                    Console.WriteLine("Waiting 2 seconds for grain activation before sending first issue...");
                    await Task.Delay(2000);
                }
                
                // Publish to both streams for redundancy
                Console.WriteLine($"Publishing to MemoryStreams provider...");
                await issuesStream.OnNextAsync(gitHubIssueEvent);
                
                Console.WriteLine($"Publishing to Aevatar provider...");
                await aevatarIssuesStream.OnNextAsync(gitHubIssueEvent);
                
                // Small delay to make sure events are processed properly
                await Task.Delay(200);  // Increased delay between events
            }

            Console.WriteLine("All issues published for analysis.");
            Console.WriteLine("Waiting for final results (press Enter to continue)...");
            Console.ReadLine();

            // Clean up subscriptions
            await memorySubscription.UnsubscribeAsync();
            await aevatarSubscription.UnsubscribeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error publishing issues: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error analyzing repository: {ex.Message}");
        Console.WriteLine($"Stack trace: {ex.StackTrace}");
    }
} 