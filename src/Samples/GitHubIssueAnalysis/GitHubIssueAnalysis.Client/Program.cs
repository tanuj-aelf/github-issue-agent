using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;

using ClientGitHubIssueInfo = GitHubIssueAnalysis.GAgents.GrainInterfaces.Models.GitHubIssueInfo;
using ClientGitHubIssueEvent = GitHubIssueAnalysis.GAgents.GrainInterfaces.Models.GitHubIssueEvent;
using ClientRepositorySummaryReport = GitHubIssueAnalysis.GAgents.GrainInterfaces.Models.RepositorySummaryReport;
using AnalysisGitHubIssueEvent = GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubIssueEvent;

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

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

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
        clientBuilder.AddMemoryStreams("MemoryStreams");
        clientBuilder.AddMemoryStreams("Aevatar");
    })
    .ConfigureServices((hostContext, services) => 
    {
        services.AddTransient<GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient>(provider => 
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            string personalAccessToken = configuration["GitHub:PersonalAccessToken"] ?? "";
            
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

await MainAsync(host);

static async Task MainAsync(IHost host)
{
    try 
    {
        await host.StartAsync();

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
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in MainAsync: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}

static async Task PublishIssuesToGAgent(
    IClusterClient clusterClient, 
    List<ClientGitHubIssueInfo> issues, 
    string memoryStreamProviderName = "MemoryStreams")
{
    Console.WriteLine("\nProcessing issues...");
    int processedCount = 0;

    Guid analysisGrainId = Guid.Parse("E0E7DE68-438C-4DE3-9C4E-F6D52D87559E");
    var analysisGrain = clusterClient.GetGrain<GitHubIssueAnalysis.GAgents.GitHubAnalysis.IGitHubAnalysisGAgent>(analysisGrainId);
    Console.WriteLine($"Using analysis grain with ID: {analysisGrainId}");
    
    try
    {
        Console.WriteLine("Waiting 2 seconds for grain activation before sending first issue...");
        await Task.Delay(2000);

        foreach (var issue in issues)
        {
            Console.WriteLine($"Publishing issue: {issue.Title} (#{issue.Id})");
            
            try
            {
                var commonIssue = new GitHubIssueAnalysis.GAgents.Common.GitHubIssueInfo
                {
                    Id = issue.Id,
                    Title = issue.Title,
                    Description = issue.Description,
                    Status = issue.Status,
                    Repository = issue.Repository,
                    Url = issue.Url,
                    CreatedAt = issue.CreatedAt,
                    Labels = issue.Labels
                };
                
                var gitHubIssueEvent = new AnalysisGitHubIssueEvent 
                { 
                    IssueInfo = commonIssue
                };
                
                await analysisGrain.HandleGitHubIssueEventAsync(gitHubIssueEvent);
                Console.WriteLine("Successfully published via direct grain call");
                
                processedCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error publishing issue #{issue.Id}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            
            await Task.Delay(500);
        }
        
        Console.WriteLine("\nRequesting summary analysis...");
        await analysisGrain.GenerateSummaryReportAsync(issues.First().Repository);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nError in publishing process: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }

    Console.WriteLine($"\nAll issues published for analysis. Successfully processed {processedCount} of {issues.Count} issues.");
}

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

        List<GitHubIssueAnalysis.GAgents.Common.GitHubIssueInfo> rawIssues;
        List<ClientGitHubIssueInfo> issues = new List<ClientGitHubIssueInfo>();
        
        try
        {
            rawIssues = await gitHubClient.GetRepositoryIssuesAsync(owner, repo, maxIssues, issueState);
            
            Console.WriteLine($"Found {rawIssues.Count} issues. Starting analysis...");

            if (rawIssues.Count == 0)
            {
                Console.WriteLine("\n⚠️ No issues found for this repository. This could be because:");
                Console.WriteLine(" - The repository doesn't have any issues with state: " + issueState);
                Console.WriteLine(" - All entries might be pull requests rather than issues");
                Console.WriteLine(" - There might be permission issues accessing the repository");
                Console.WriteLine("\nPlease try another repository, issue state, or check the repository name.");
                return;
            }
            
            issues = rawIssues.Select(issue => new ClientGitHubIssueInfo
            {
                Id = issue.Id,
                Title = issue.Title,
                Description = issue.Description,
                Status = issue.Status,
                State = issue.Status,
                Url = issue.Url,
                Repository = issue.Repository,
                CreatedAt = issue.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                ClosedAt = issue.Status?.ToLower() == "closed" ? (DateTime?)DateTime.UtcNow : null,
                Labels = issue.Labels
            }).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ Error fetching issues: {ex.Message}");
            Console.WriteLine("Please try another repository or check your connection.");
            return;
        }

        Console.WriteLine("\nIssues found for analysis:");
        Console.WriteLine("---------------------------");
        int openCount = 0;
        int closedCount = 0;
        
        foreach (var issue in issues)
        {
            Console.WriteLine($"#{issue.Id}: {issue.Title}");
            Console.WriteLine($"  Status: {issue.Status}");
            
            if (issue.Status?.ToLower() == "open")
                openCount++;
            else if (issue.Status?.ToLower() == "closed")
                closedCount++;
                
            Console.WriteLine($"  Labels: {string.Join(", ", issue.Labels)}");
            Console.WriteLine($"  URL: {issue.Url}");
            Console.WriteLine();
        }
        
        Console.WriteLine("---------------------------");
        Console.WriteLine($"Open issues: {openCount}, Closed issues: {closedCount}, Total: {issues.Count}");
        if (issueState != "all" && ((issueState == "open" && closedCount > 0) || (issueState == "closed" && openCount > 0)))
        {
            Console.WriteLine("WARNING: Found issues with state not matching the requested state.");
            Console.WriteLine("This could indicate an issue with state filtering.");
        }
        Console.WriteLine("---------------------------\n");

        Console.WriteLine("Setting up stream subscriptions for summary reports...");

        var summaryStreamId = StreamId.Create(GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubAnalysisStream.StreamNamespace, 
                                            GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubAnalysisStream.SummaryStreamKey);
        Console.WriteLine($"Subscribing to summary stream: {GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubAnalysisStream.StreamNamespace}/{GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubAnalysisStream.SummaryStreamKey}");
        
        Console.WriteLine("Creating subscription with MemoryStreams provider...");
        var memoryStreamProvider = clusterClient.GetStreamProvider("MemoryStreams");
        var memoryStream = memoryStreamProvider.GetStream<ClientRepositorySummaryReport>(summaryStreamId);
        var memorySubscription = await memoryStream.SubscribeAsync(
            (summaryReport, token) =>
            {
                Console.WriteLine("\n===============================================================");
                Console.WriteLine("              GitHub ISSUE ANALYSIS RESULTS                    ");
                Console.WriteLine("===============================================================");
                Console.WriteLine($"Repository: {summaryReport.Repository}");
                Console.WriteLine($"Total Issues Analyzed: {summaryReport.TotalIssues}");
                Console.WriteLine($"Open Issues: {summaryReport.OpenIssues}, Closed Issues: {summaryReport.ClosedIssues}");
                Console.WriteLine($"Analysis Completed: {summaryReport.GeneratedAt:g}");
                
                Console.WriteLine("\n=== EXTRACTED THEMES ===");
                Console.WriteLine("The following themes were identified in the repository issues:");
                
                if (summaryReport.TopTags != null && summaryReport.TopTags.Length > 0)
                {
                    int maxTagWidth = Math.Max(12, summaryReport.TopTags.Select(t => t.Tag.Length).DefaultIfEmpty(0).Max());
                    
                    Console.WriteLine($"\n{"TAG".PadRight(maxTagWidth)} | {"COUNT",-5} | {"PERCENTAGE",-10} | {"GRAPH",-20}");
                    Console.WriteLine(new string('-', maxTagWidth + 42));
                    
                    foreach (var tag in summaryReport.TopTags)
                    {
                        double percentage = (double)tag.Count / summaryReport.TotalIssues * 100;
                        int barLength = (int)(percentage / 5);
                        string bar = new string('█', Math.Min(barLength, 20));
                        
                        Console.WriteLine($"{tag.Tag.PadRight(maxTagWidth)} | {tag.Count,-5} | {percentage,9:F1}% | {bar}");
                    }
                }
                else
                {
                    Console.WriteLine("No tags were extracted from the issues.");
                }
                
                if (summaryReport.Recommendations != null && summaryReport.Recommendations.Length > 0)
                {
                    Console.WriteLine("\n=== RECOMMENDATIONS ===");
                    int i = 1;
                    foreach (var rec in summaryReport.Recommendations)
                    {
                        Console.WriteLine($"{i}. {rec.Title} [{rec.Priority}]");
                        Console.WriteLine($"   {rec.Description}");
                        Console.WriteLine();
                        i++;
                    }
                }
                else 
                {
                    Console.WriteLine("\n=== RECOMMENDATIONS ===");
                    Console.WriteLine("No specific recommendations were generated.");
                }
                
                Console.WriteLine("===============================================================");
                
                return Task.CompletedTask;
            });
        Console.WriteLine("Successfully created subscription to summary stream with MemoryStreams");

        try
        {
            Console.WriteLine("Creating subscription with Aevatar provider...");
            var aevatarStreamProvider = clusterClient.GetStreamProvider("Aevatar");
            var aevatarStream = aevatarStreamProvider.GetStream<ClientRepositorySummaryReport>(summaryStreamId);
            var aevatarSubscription = await aevatarStream.SubscribeAsync(
                (summaryReport, token) =>
                {
                    Console.WriteLine("Received summary report through Aevatar provider");
                    return Task.CompletedTask;
                });
            Console.WriteLine("Successfully created subscription to summary stream with Aevatar");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create subscription with Aevatar provider: {ex.Message}");
        }

        await PublishIssuesToGAgent(clusterClient, issues, "MemoryStreams");

        Console.WriteLine("\nWaiting for the analysis to complete...");
        Console.WriteLine("This may take up to 30 seconds for the LLM to process the data.");
        
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(5000);
            Console.WriteLine("..... Still processing");
        }
        
        Console.WriteLine("\nAnalysis should be complete. If results aren't displayed above, press Enter to continue...");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error during analysis: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}