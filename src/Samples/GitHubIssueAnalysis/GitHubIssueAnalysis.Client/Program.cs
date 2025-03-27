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
    })
    .ConfigureServices((hostContext, services) => 
    {
        // Register our GitHub client with configuration
        services.AddTransient<GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient>(provider => 
        {
            // Use configuration for PAT if available
            string personalAccessToken = hostContext.Configuration["GitHub:PersonalAccessToken"] ?? "";
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
        var streamId = StreamId.Create("GitHubAnalysisStream", Guid.NewGuid().ToString());
        var stream = streamProvider.GetStream<SummaryReportEvent>(streamId);

        // Subscribe to the stream
        var subscription = await stream.SubscribeAsync((summaryEvent, token) =>
        {
            Console.WriteLine("\n=============================================");
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

        Console.WriteLine("Listening for summary report events...");

        // Get an instance of the GAgent via the cluster client
        try 
        {
            // Instead of using the GAgent directly, publish events to a well-known stream
            var publishStream = streamProvider.GetStream<GitHubIssueEvent>("GitHubAnalysisStream", "issues");
            
            // Process each issue
            foreach (var issue in issues)
            {
                Console.WriteLine($"Publishing issue: {issue.Title}");
                
                // Create the event to send to the grain
                var gitHubIssueEvent = new GitHubIssueEvent 
                { 
                    IssueInfo = issue 
                };
                
                // Publish to the stream instead of calling the grain directly
                await publishStream.OnNextAsync(gitHubIssueEvent);
                
                // Small delay to make sure events are processed properly
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }

        Console.WriteLine("All issues published for analysis.");
        Console.WriteLine("Waiting for final results (press Enter to continue)...");
        Console.ReadLine();

        // Clean up subscription
        await subscription.UnsubscribeAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error analyzing repository: {ex.Message}");
        Console.WriteLine($"Stack trace: {ex.StackTrace}");
    }
} 