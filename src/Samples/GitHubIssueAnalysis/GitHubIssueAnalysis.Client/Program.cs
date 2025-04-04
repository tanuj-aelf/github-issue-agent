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
using System.Linq;
using System.Collections.Generic;
using System.Text;

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
    try
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
        
        // Add option to select issue state
        Console.WriteLine("\nSelect which issues to analyze:");
        Console.WriteLine("1) Open issues only");
        Console.WriteLine("2) Closed issues only");
        Console.WriteLine("3) All issues (both open and closed)");
        Console.Write("Your choice (default 3): ");
        
        string issueState = "all";
        string stateChoice = Console.ReadLine()?.Trim() ?? "3";
        
        switch (stateChoice)
        {
            case "1":
                issueState = "open";
                Console.WriteLine("Analyzing open issues only.");
                break;
            case "2":
                issueState = "closed";
                Console.WriteLine("Analyzing closed issues only.");
                break;
            case "3":
            default:
                issueState = "all";
                Console.WriteLine("Analyzing all issues (both open and closed).");
                break;
        }

        Console.WriteLine($"\nFetching up to {maxIssues} {issueState} issues from {owner}/{repo}...");

        // Fetch issues from GitHub with better error handling
        List<GitHubIssueInfo> issues;
        try
        {
            issues = await gitHubClient.GetRepositoryIssuesAsync(owner, repo, maxIssues, issueState);
            
            Console.WriteLine($"Found {issues.Count} issues. Starting analysis...");

            if (issues.Count == 0)
            {
                Console.WriteLine("\n⚠️ No issues found for this repository. This could be because:");
                Console.WriteLine(" - The repository doesn't have any issues with state: " + issueState);
                Console.WriteLine(" - All entries might be pull requests rather than issues");
                Console.WriteLine(" - There might be permission issues accessing the repository");
                Console.WriteLine("\nPlease try another repository, issue state, or check the repository name.");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ Error fetching issues: {ex.Message}");
            Console.WriteLine("Please try another repository or check your connection.");
            return;
        }

        // Display the issues we found to give the user visibility
        Console.WriteLine("\nIssues found for analysis:");
        Console.WriteLine("---------------------------");
        foreach (var issue in issues)
        {
            Console.WriteLine($"#{issue.Id}: {issue.Title}");
            Console.WriteLine($"  Status: {issue.Status}");
            Console.WriteLine($"  Labels: {string.Join(", ", issue.Labels)}");
            Console.WriteLine($"  URL: {issue.Url}");
            Console.WriteLine();
        }
        Console.WriteLine("---------------------------\n");

        // Set up stream subscription for the event types
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
                Console.WriteLine("\n===============================================================");
                Console.WriteLine("              GitHub ISSUE ANALYSIS RESULTS                    ");
                Console.WriteLine("===============================================================");
                Console.WriteLine($"Repository: {summaryEvent.Repository}");
                Console.WriteLine($"Total Issues Analyzed: {summaryEvent.TotalIssuesAnalyzed}");
                Console.WriteLine($"Analysis Completed: {summaryEvent.GeneratedAt.ToString("g")}");
                
                Console.WriteLine("\n=== EXTRACTED THEMES ===");
                Console.WriteLine("The following themes were identified in the repository issues:");
                
                // Format tag frequencies into a nice table with percentages
                var sortedTags = summaryEvent.TagFrequency.OrderByDescending(t => t.Value).ToList();
                int maxTagWidth = Math.Max(12, sortedTags.Select(t => t.Key.Length).DefaultIfEmpty(0).Max());
                
                Console.WriteLine($"\n{"TAG".PadRight(maxTagWidth)} | {"COUNT",-5} | {"PERCENTAGE",-10} | {"GRAPH",-20}");
                Console.WriteLine(new string('-', maxTagWidth + 42));
                
                foreach (var tag in sortedTags)
                {
                    double percentage = (double)tag.Value / summaryEvent.TotalIssuesAnalyzed * 100;
                    int barLength = (int)(percentage / 5); // 20 chars = 100%
                    string bar = new string('█', Math.Min(barLength, 20));
                    
                    Console.WriteLine($"{tag.Key.PadRight(maxTagWidth)} | {tag.Value,-5} | {percentage,9:F1}% | {bar}");
                }
                
                Console.WriteLine("\n=== PRIORITY RECOMMENDATIONS ===");
                Console.WriteLine("Based on the analysis, we recommend focusing on:");
                
                for (int i = 0; i < summaryEvent.PriorityRecommendations.Count; i++)
                {
                    Console.WriteLine($"{i+1}. {summaryEvent.PriorityRecommendations[i]}");
                }
                
                Console.WriteLine("\n=== ANALYZED ISSUES ===");
                Console.WriteLine("The following issues were analyzed:");
                
                // Group issues by their tags for better insight
                var issuesByTag = new Dictionary<string, List<IssueDetails>>();
                foreach (var issue in summaryEvent.AnalyzedIssues)
                {
                    foreach (var tag in issue.Tags)
                    {
                        if (!issuesByTag.ContainsKey(tag))
                        {
                            issuesByTag[tag] = new List<IssueDetails>();
                        }
                        issuesByTag[tag].Add(issue);
                    }
                }
                
                // Display top 3 most frequent tags with their issues
                var topTags = summaryEvent.TagFrequency.OrderByDescending(t => t.Value).Take(3).Select(t => t.Key).ToList();
                
                foreach (var tag in topTags)
                {
                    if (issuesByTag.ContainsKey(tag))
                    {
                        Console.WriteLine($"\nIssues tagged with '{tag}':");
                        foreach (var issue in issuesByTag[tag].Take(5)) // Show up to 5 issues per tag
                        {
                            Console.WriteLine($"  - #{issue.Id}: {issue.Title}");
                            Console.WriteLine($"    URL: {issue.Url}");
                        }
                        
                        if (issuesByTag[tag].Count > 5)
                        {
                            Console.WriteLine($"    ... and {issuesByTag[tag].Count - 5} more");
                        }
                    }
                }
                
                Console.WriteLine("\n===============================================================");
                Console.WriteLine("                     END OF ANALYSIS                          ");
                Console.WriteLine("===============================================================");
                
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
                Console.WriteLine("\n===============================================================");
                Console.WriteLine("              GitHub ISSUE ANALYSIS RESULTS                    ");
                Console.WriteLine("===============================================================");
                Console.WriteLine($"Repository: {summaryEvent.Repository}");
                Console.WriteLine($"Total Issues Analyzed: {summaryEvent.TotalIssuesAnalyzed}");
                Console.WriteLine($"Analysis Completed: {summaryEvent.GeneratedAt.ToString("g")}");
                
                Console.WriteLine("\n=== EXTRACTED THEMES ===");
                Console.WriteLine("The following themes were identified in the repository issues:");
                
                // Format tag frequencies into a nice table with percentages
                var sortedTags = summaryEvent.TagFrequency.OrderByDescending(t => t.Value).ToList();
                int maxTagWidth = Math.Max(12, sortedTags.Select(t => t.Key.Length).DefaultIfEmpty(0).Max());
                
                Console.WriteLine($"\n{"TAG".PadRight(maxTagWidth)} | {"COUNT",-5} | {"PERCENTAGE",-10} | {"GRAPH",-20}");
                Console.WriteLine(new string('-', maxTagWidth + 42));
                
                foreach (var tag in sortedTags)
                {
                    double percentage = (double)tag.Value / summaryEvent.TotalIssuesAnalyzed * 100;
                    int barLength = (int)(percentage / 5); // 20 chars = 100%
                    string bar = new string('█', Math.Min(barLength, 20));
                    
                    Console.WriteLine($"{tag.Key.PadRight(maxTagWidth)} | {tag.Value,-5} | {percentage,9:F1}% | {bar}");
                }
                
                Console.WriteLine("\n=== PRIORITY RECOMMENDATIONS ===");
                Console.WriteLine("Based on the analysis, we recommend focusing on:");
                
                for (int i = 0; i < summaryEvent.PriorityRecommendations.Count; i++)
                {
                    Console.WriteLine($"{i+1}. {summaryEvent.PriorityRecommendations[i]}");
                }
                
                Console.WriteLine("\n=== ANALYZED ISSUES ===");
                Console.WriteLine("The following issues were analyzed:");
                
                // Group issues by their tags for better insight
                var issuesByTag = new Dictionary<string, List<IssueDetails>>();
                foreach (var issue in summaryEvent.AnalyzedIssues)
                {
                    foreach (var tag in issue.Tags)
                    {
                        if (!issuesByTag.ContainsKey(tag))
                        {
                            issuesByTag[tag] = new List<IssueDetails>();
                        }
                        issuesByTag[tag].Add(issue);
                    }
                }
                
                // Display top 3 most frequent tags with their issues
                var topTags = summaryEvent.TagFrequency.OrderByDescending(t => t.Value).Take(3).Select(t => t.Key).ToList();
                
                foreach (var tag in topTags)
                {
                    if (issuesByTag.ContainsKey(tag))
                    {
                        Console.WriteLine($"\nIssues tagged with '{tag}':");
                        foreach (var issue in issuesByTag[tag].Take(5)) // Show up to 5 issues per tag
                        {
                            Console.WriteLine($"  - #{issue.Id}: {issue.Title}");
                            Console.WriteLine($"    URL: {issue.Url}");
                        }
                        
                        if (issuesByTag[tag].Count > 5)
                        {
                            Console.WriteLine($"    ... and {issuesByTag[tag].Count - 5} more");
                        }
                    }
                }
                
                Console.WriteLine("\n===============================================================");
                Console.WriteLine("                     END OF ANALYSIS                          ");
                Console.WriteLine("===============================================================");
                
                return Task.CompletedTask;
            });
        Console.WriteLine("Successfully created subscription to summary stream with Aevatar");

        try
        {
            // Get the stream for publishing issues using the correct namespace and ID
            var issuesStreamId = StreamId.Create("GitHubAnalysisStream", Guid.Parse("22222222-2222-2222-2222-222222222222"));
            Console.WriteLine($"Publishing issues to stream: GitHubAnalysisStream/22222222-2222-2222-2222-222222222222");
            var issuesStream = memoryStreamProvider.GetStream<GitHubIssueEvent>(issuesStreamId);
            
            // Process each issue - we're now just using one stream to avoid duplicate processing
            Console.WriteLine("\nProcessing issues...");
            int processedCount = 0;
            
            foreach (var issue in issues)
            {
                Console.WriteLine($"Publishing issue: {issue.Title} (#{issue.Id})");
                
                // Create the event to send 
                var gitHubIssueEvent = new GitHubIssueEvent 
                { 
                    IssueInfo = issue 
                };
                
                // Add extra delay to allow the grain to activate and subscribe
                if (issue == issues.First())
                {
                    Console.WriteLine("Waiting 2 seconds for grain activation before sending first issue...");
                    await Task.Delay(2000);
                }
                
                try
                {
                    Console.WriteLine("Publishing to MemoryStreams provider...");
                    await issuesStream.OnNextAsync(gitHubIssueEvent);
                    Console.WriteLine("Successfully published to MemoryStreams");
                    processedCount++;
                    
                    // Add delay between issues to avoid overwhelming the agent - increased for better processing
                    await Task.Delay(1500); 
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error publishing issue: {ex.Message}");
                }
            }
            
            Console.WriteLine($"\nAll issues published for analysis. Successfully processed {processedCount} of {issues.Count} issues.");
            
            // Add a more informative wait message with counter
            Console.WriteLine("\nWaiting for the analysis to complete...");
            Console.WriteLine("This may take up to 30 seconds for the LLM to process the data.");
            
            // Display a simple progress indicator
            for (int i = 0; i < 15; i++)
            {
                Console.Write(".");
                await Task.Delay(1000);
                
                // Every 5 seconds, give a status update
                if (i % 5 == 4)
                {
                    Console.WriteLine(" Still processing");
                }
            }
            
            Console.WriteLine("\nAnalysis should be complete. If results aren't displayed above, press Enter to continue...");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during stream setup or publishing: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"An unexpected error occurred: {ex.Message}");
    }
} 