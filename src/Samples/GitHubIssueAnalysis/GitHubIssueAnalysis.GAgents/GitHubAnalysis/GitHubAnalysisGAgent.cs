using Aevatar.Core;
using Aevatar.Core.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using GitHubIssueAnalysis.GAgents.Common;
using GitHubIssueAnalysis.GAgents.Services;
using Orleans.Streams;
using Orleans.Runtime;
using Orleans;
using Orleans.Concurrency;
using Orleans.Timers;
using Orleans.Runtime.Scheduler;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System;
using GitHubIssueAnalysis.GAgents.GrainInterfaces.Models;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

// Remove the ImplicitStreamSubscription attribute
[Reentrant]
public class GitHubAnalysisGAgent : GAgentBase<GitHubAnalysisGAgentState, GitHubAnalysisLogEvent>, IGitHubAnalysisGAgent, IGrainWithGuidKey
{
    private readonly ILogger<GitHubAnalysisGAgent> _logger;
    private readonly ILLMService _llmService;
    
    // Use the correct type for stream subscription handle
    private StreamSubscriptionHandle<GitHubIssueEvent>? _streamSubscription;
    private IDisposable? _timer;
    
    // Constructor with improved stream subscription
    public GitHubAnalysisGAgent(
        ILogger<GitHubAnalysisGAgent> logger, 
        ILLMService llmService)
    {
        _logger = logger;
        _llmService = llmService;
        
        // Log grain creation with a more visible message
        _logger.LogWarning("====== GitHubAnalysisGAgent CREATED with ID: {GrainId} ======", this.GetPrimaryKey());
        
        // Immediate subscription attempt with no timer
        SetupStreamSubscriptionAsync().Ignore();
        
        // Initialize a timer the simple way, avoiding the obsolete API
        _timer = RegisterTimer(
            CheckSubscription,
            null,
            TimeSpan.FromSeconds(1),  // Start after 1 second
            TimeSpan.FromSeconds(5)); // Check every 5 seconds
    }
    
    private async Task CheckSubscription(object _)
    {
        if (_streamSubscription == null)
        {
            _logger.LogWarning("Retrying stream subscription from timer...");
            await SetupStreamSubscriptionAsync();
            return;
        }
        
        // Check if subscription is still active
        try
        {
            var streamProvider = GetAppropriateStreamProvider();
            var stream = streamProvider.GetStream<GitHubIssueEvent>(
                StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey));
            
            // If we have a subscription but lost connection, reconnect
            _logger.LogInformation("Checking subscription status from timer...");
        }
        catch
        {
            // If any errors, try to resubscribe
            _logger.LogWarning("Error checking subscription status, attempting to resubscribe...");
            await SetupStreamSubscriptionAsync();
        }
    }
    
    // Updated to handle stream subscription setup with retry logic
    public async Task SetupStreamSubscriptionAsync()
    {
        try
        {
            _logger.LogWarning("====== SETTING UP STREAM SUBSCRIPTION FOR GRAIN {GrainId} ======", this.GetPrimaryKey());
            
            // Clear any existing subscription
            if (_streamSubscription != null)
            {
                try
                {
                    await _streamSubscription.UnsubscribeAsync();
                    _logger.LogInformation("Unsubscribed from previous stream subscription");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to unsubscribe from previous stream subscription");
                }
                _streamSubscription = null;
            }
            
            // Try both providers in sequence
            IStreamProvider? streamProvider = null;
            try
            {
                streamProvider = this.GetStreamProvider("Aevatar");
                _logger.LogInformation("Using Aevatar stream provider");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get 'Aevatar' stream provider, trying 'MemoryStreams'");
                try
                {
                    streamProvider = this.GetStreamProvider("MemoryStreams");
                    _logger.LogInformation("Using MemoryStreams provider");
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "Failed to get any stream provider");
                    throw;
                }
            }
            
            // Only continue if we have a valid provider
            if (streamProvider != null)
            {
                // Subscribe to the main issues stream
                var issuesStreamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey);
                _logger.LogWarning("====== SUBSCRIBING TO ISSUES STREAM: {Namespace}/{Key} ======", 
                    GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey);
                
                var issuesStream = streamProvider.GetStream<GitHubIssueEvent>(issuesStreamId);
                _streamSubscription = await issuesStream.SubscribeAsync(this);
                _logger.LogWarning("====== SUCCESSFULLY SUBSCRIBED TO ISSUES STREAM ======");
                
                // Also try to subscribe to the tags stream to handle any forwarded events
                try 
                {
                    var tagsStreamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey);
                    _logger.LogWarning("====== SUBSCRIBING TO TAGS STREAM: {Namespace}/{Key} ======", 
                        GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey);
                    
                    var tagsStream = streamProvider.GetStream<IssueTagsEvent>(tagsStreamId);
                    await tagsStream.SubscribeAsync(new TagsStreamObserver(this, _logger));
                    _logger.LogWarning("====== SUCCESSFULLY SUBSCRIBED TO TAGS STREAM ======");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to subscribe to tags stream, but will continue");
                }
                
                // Also try to subscribe to the summary stream for debugging
                try 
                {
                    var summaryStreamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.SummaryStreamKey);
                    _logger.LogWarning("====== SUBSCRIBING TO SUMMARY STREAM: {Namespace}/{Key} ======", 
                        GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.SummaryStreamKey);
                    
                    var summaryStream = streamProvider.GetStream<RepositorySummaryReport>(summaryStreamId);
                    await summaryStream.SubscribeAsync(new SummaryStreamObserver(_logger));
                    _logger.LogWarning("====== SUCCESSFULLY SUBSCRIBED TO SUMMARY STREAM ======");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to subscribe to summary stream, but will continue");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set up stream subscription");
        }
    }
    
    // Helper class to observe tags stream events
    private class TagsStreamObserver : IAsyncObserver<IssueTagsEvent>
    {
        private readonly GitHubAnalysisGAgent _agent;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        
        public TagsStreamObserver(GitHubAnalysisGAgent agent, Microsoft.Extensions.Logging.ILogger logger)
        {
            _agent = agent;
            _logger = logger;
        }
        
        public Task OnNextAsync(IssueTagsEvent item, StreamSequenceToken? token = null)
        {
            try
            {
                _logger.LogWarning("Received tags event for issue: {IssueId}", item.IssueId);
                
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"\n\n========== RECEIVED TAGS EVENT AT {timestamp} ==========");
                Console.ResetColor();
                
                Console.WriteLine($"Repository: {item.Repository}");
                Console.WriteLine($"Issue: #{item.IssueId} - {item.Title}");
                Console.WriteLine($"Extraction Time: {DateTime.UtcNow}");
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nEXTRACTED TAGS:");
                Console.ResetColor();
                
                if (item.ExtractedTags != null && item.ExtractedTags.Length > 0)
                {
                    foreach (var tag in item.ExtractedTags)
                    {
                        Console.WriteLine($"  - {tag}");
                    }
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"\nTotal Tags: {item.ExtractedTags.Length}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  No tags extracted");
                    Console.ResetColor();
                }
                
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine("========== END OF TAGS EVENT ==========\n");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR in TagsStreamObserver: {ex.Message}");
                Console.ResetColor();
                _logger.LogError(ex, "Error processing tags event in stream observer");
            }
            
            return Task.CompletedTask;
        }
        
        public Task OnCompletedAsync()
        {
            _logger.LogInformation("Tags stream completed");
            return Task.CompletedTask;
        }
        
        public Task OnErrorAsync(Exception ex)
        {
            _logger.LogError(ex, "Error in tags stream");
            return Task.CompletedTask;
        }
    }
    
    // Helper class to observe summary stream events
    private class SummaryStreamObserver : IAsyncObserver<RepositorySummaryReport>
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        
        public SummaryStreamObserver(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger;
        }
        
        public Task OnNextAsync(RepositorySummaryReport item, StreamSequenceToken? token = null)
        {
            try
            {
                _logger.LogWarning("====================================================");
                _logger.LogWarning("    RECEIVED SUMMARY REPORT FROM STREAM            ");
                _logger.LogWarning("====================================================");
                _logger.LogWarning("Repository: {Repository}", item.Repository);
                _logger.LogWarning("Total Issues: {TotalIssues}", item.TotalIssues);
                _logger.LogWarning("Open Issues: {OpenIssues}", item.OpenIssues);
                _logger.LogWarning("Closed Issues: {ClosedIssues}", item.ClosedIssues);
                _logger.LogWarning("Generated At: {GeneratedAt}", item.GeneratedAt);
                _logger.LogWarning("Stream Token: {Token}", token);
                _logger.LogWarning("====================================================");
                
                // Console display for the client
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"\n\n========== RECEIVED REPOSITORY ANALYSIS AT {timestamp} ==========");
                Console.ResetColor();
                
                Console.WriteLine($"Repository: {item.Repository}");
                Console.WriteLine($"Total Issues: {item.TotalIssues} ({item.OpenIssues} open, {item.ClosedIssues} closed)");
                Console.WriteLine($"Generated: {item.GeneratedAt}");
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nTOP TAGS:");
                Console.ResetColor();
                
                if (item.TopTags != null && item.TopTags.Length > 0)
                {
                    foreach (var tag in item.TopTags)
                    {
                        Console.WriteLine($"  - {tag.Tag}: {tag.Count} issue(s)");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  No tags found");
                    Console.ResetColor();
                }
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nRECOMMENDATIONS:");
                Console.ResetColor();
                
                if (item.Recommendations != null && item.Recommendations.Length > 0)
                {
                    foreach (var rec in item.Recommendations)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"\n* {rec.Title} (Priority: {rec.Priority})");
                        Console.ResetColor();
                        
                        if (rec.SupportingIssues != null && rec.SupportingIssues.Length > 0)
                        {
                            Console.WriteLine($"  Supporting Issues: {string.Join(", ", rec.SupportingIssues.Select(i => $"#{i}"))}");
                        }
                        else
                        {
                            Console.WriteLine("  Supporting Issues: None specified");
                        }
                        Console.WriteLine($"  {rec.Description}");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  No recommendations generated");
                    Console.ResetColor();
                }
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nISSUE ACTIVITY:");
                Console.ResetColor();
                
                if (item.TimeRanges != null && item.TimeRanges.Length > 0)
                {
                    foreach (var range in item.TimeRanges)
                    {
                        Console.WriteLine($"  {range.StartDate:yyyy-MM-dd} to {range.EndDate:yyyy-MM-dd}: {range.IssuesCreated} created, {range.IssuesClosed} closed");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  No activity data available");
                    Console.ResetColor();
                }
                
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine("\n========== END OF REPOSITORY ANALYSIS ==========\n");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR in SummaryStreamObserver: {ex.Message}");
                Console.ResetColor();
                _logger.LogError(ex, "Error processing summary report in stream observer");
            }
            
            return Task.CompletedTask;
        }
        
        public Task OnCompletedAsync()
        {
            _logger.LogInformation("Summary stream completed");
            return Task.CompletedTask;
        }
        
        public Task OnErrorAsync(Exception ex)
        {
            _logger.LogError(ex, "Error in summary stream");
            return Task.CompletedTask;
        }
    }

    // Remove the override and create a helper method instead
    private IStreamProvider GetAppropriateStreamProvider()
    {
        // Try to get the preferred stream provider, with fallback options
        try
        {
            _logger.LogInformation("Attempting to get the primary stream provider");
            return this.GetStreamProvider("Aevatar");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get Aevatar stream provider, falling back to MemoryStreams");
            
            try
            {
                return this.GetStreamProvider("MemoryStreams");
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Failed to get MemoryStreams provider, using default Orleans.Streams.MemoryStream");
                return this.GetStreamProvider("Orleans.Streams.MemoryStream");
            }
        }
    }

    // Explicit handler for ImplicitStreamSubscription 
    public async Task OnNextAsync(GitHubIssueEvent @event, StreamSequenceToken? token = null)
    {
        try
        {
            // Add a timestamp and more visible event notification
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n\n********************************************************************");
            Console.WriteLine($"*          EVENT RECEIVED BY ANALYZER AT {timestamp}           *");
            Console.WriteLine("********************************************************************");
            Console.ResetColor();
            
            Console.WriteLine($"REPOSITORY: {@event.IssueInfo.Repository}");
            Console.WriteLine($"ISSUE: #{@event.IssueInfo.Id} - {@event.IssueInfo.Title}");
            Console.WriteLine($"STATUS: {@event.IssueInfo.Status}");
            Console.WriteLine($"RECEIVED AT: {DateTime.UtcNow}");
            Console.WriteLine($"GRAIN ID: {this.GetPrimaryKey()}");
            Console.WriteLine("********************************************************************");
            
            _logger.LogWarning("====================================================");
            _logger.LogWarning("    RECEIVED EVENT FROM STREAM                     ");
            _logger.LogWarning("====================================================");
            _logger.LogWarning("Issue Title: {@IssueTitle}", @event.IssueInfo.Title);
            _logger.LogWarning("Repository: {@Repository}", @event.IssueInfo.Repository);
            _logger.LogWarning("Stream Token: {Token}", token);
            _logger.LogWarning("Grain ID: {GrainId}", this.GetPrimaryKey());
            _logger.LogWarning("====================================================");
            
            // Process the event immediately
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\nBEGINNING ISSUE ANALYSIS - THIS MAY TAKE UP TO 30 SECONDS\n");
            Console.ResetColor();
            
            await HandleGitHubIssueEventInternalAsync(@event);
            
            // Force summary generation after each event for immediate feedback
            try
            {
                string repository = @event.IssueInfo.Repository;
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nFORCING SUMMARY GENERATION FOR IMMEDIATE FEEDBACK...");
                Console.ResetColor();
                
                _logger.LogWarning("Forcing summary generation after receiving event for repository: {Repository}", repository);
                
                // Wait a moment to ensure tags have been processed
                await Task.Delay(1000);
                
                // Generate summary report
                await GenerateSummaryReportAsync(repository);
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nSUMMARY REPORT GENERATED AND PUBLISHED TO STREAM\n");
                Console.ResetColor();
                
                _logger.LogWarning("Summary report generated and should be published to stream");
                
                // Output clear completion message
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n********************************************************************");
                Console.WriteLine("*               COMPLETED GITHUB ISSUE ANALYSIS                    *");
                Console.WriteLine("*                                                                  *");
                Console.WriteLine("*        Results have been published to the summary stream         *");
                Console.WriteLine("*     You should see the detailed analysis output above/below      *");
                Console.WriteLine("********************************************************************\n");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nERROR IN SUMMARY GENERATION: {ex.Message}");
                Console.ResetColor();
                _logger.LogError(ex, "Error generating forced summary after event processing");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nCRITICAL ERROR IN EVENT HANDLER: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Console.ResetColor();
            _logger.LogError(ex, "Error in OnNextAsync handler for GitHubIssueEvent");
        }
    }

    public override Task<string> GetDescriptionAsync()
    {
        return Task.FromResult("GitHub Issue Analysis Agent: Analyzes GitHub repository issues, extracts common themes, and helps prioritize development work.");
    }

    [EventHandler]
    public async Task HandleGitHubIssueEventAsync(GitHubIssueEvent @event)
    {
        try
        {
            _logger.LogInformation("Starting to handle GitHub issue event for issue: {@IssueTitle}", @event.IssueInfo.Title);
            await HandleGitHubIssueEventInternalAsync(@event);
            _logger.LogInformation("Successfully processed GitHub issue event for issue: {@IssueTitle}", @event.IssueInfo.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ERROR IN GITHUB ISSUE HANDLER: Failed to process GitHub issue {@IssueTitle}", @event.IssueInfo.Title);
        }
    }

    // This is now called from the stream subscription
    private async Task HandleGitHubIssueEventInternalAsync(GitHubIssueEvent @event)
    {
        try
        {
            Console.WriteLine($"\n\n======== PROCESSING GITHUB ISSUE EVENT ========");
            Console.WriteLine($"Repository: {@event.IssueInfo.Repository}");
            Console.WriteLine($"Issue: #{@event.IssueInfo.Id} - {@event.IssueInfo.Title}");
            Console.WriteLine($"Status: {@event.IssueInfo.Status}");
            
            _logger.LogInformation($"{nameof(GitHubAnalysisGAgent)} received {nameof(GitHubIssueEvent)} for repository: {@event.IssueInfo.Repository}");

            var issueInfo = @event.IssueInfo;
            
            // Convert to our model type if needed
            Console.WriteLine("\nConverting issue to model...");
            GrainInterfaces.Models.GitHubIssueInfo modelIssueInfo = new GrainInterfaces.Models.GitHubIssueInfo
            {
                Id = issueInfo.Id,
                Title = issueInfo.Title,
                Description = issueInfo.Description,
                Status = issueInfo.Status,
                State = issueInfo.Status,
                Url = issueInfo.Url,
                Repository = issueInfo.Repository,
                CreatedAt = issueInfo.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
                ClosedAt = issueInfo.Status?.ToLower() == "closed" ? (DateTime?)DateTime.UtcNow : null,
                Labels = issueInfo.Labels
            };
            
            // Store issue in state
            Console.WriteLine("\nStoring issue in state...");
            if (!State.RepositoryIssues.ContainsKey(modelIssueInfo.Repository))
            {
                Console.WriteLine($"Creating new repository entry for {modelIssueInfo.Repository}");
                _logger.LogInformation("Creating new repository entry for {Repository}", modelIssueInfo.Repository);
                State.RepositoryIssues[modelIssueInfo.Repository] = new List<GrainInterfaces.Models.GitHubIssueInfo>();
            }
            
            // Check if we already have this issue to avoid duplicates
            if (!State.RepositoryIssues[modelIssueInfo.Repository].Any(i => i.Id == modelIssueInfo.Id))
            {
                Console.WriteLine($"Adding new issue #{modelIssueInfo.Id} to repository {modelIssueInfo.Repository}");
                _logger.LogInformation("Adding new issue #{IssueId} to repository {Repository}", modelIssueInfo.Id, modelIssueInfo.Repository);
                State.RepositoryIssues[modelIssueInfo.Repository].Add(modelIssueInfo);
            }
            else
            {
                Console.WriteLine($"Issue #{modelIssueInfo.Id} already exists, updating");
                _logger.LogInformation("Issue #{IssueId} already exists in repository {Repository}, updating", modelIssueInfo.Id, modelIssueInfo.Repository);
                
                // Update the issue if it exists
                var index = State.RepositoryIssues[modelIssueInfo.Repository].FindIndex(i => i.Id == modelIssueInfo.Id);
                if (index >= 0)
                {
                    State.RepositoryIssues[modelIssueInfo.Repository][index] = modelIssueInfo;
                }
            }

            // Extract tags from the issue using LLM
            Console.WriteLine("\nExtracting tags from issue...");
            string[] extractedTags;
            try
            {
                _logger.LogInformation("Attempting to extract tags using LLM for issue: {IssueTitle} (#{IssueId})", modelIssueInfo.Title, modelIssueInfo.Id);
                extractedTags = await ExtractTagsUsingLLMAsync(modelIssueInfo);
                Console.WriteLine($"Successfully extracted {extractedTags.Length} tags");
                _logger.LogInformation("Successfully extracted {Count} tags for issue #{IssueId}", extractedTags.Length, modelIssueInfo.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to extract tags using LLM: {ex.Message}");
                Console.WriteLine("Falling back to basic extraction...");
                _logger.LogError(ex, "Failed to extract tags using LLM, falling back to basic extraction");
                extractedTags = ExtractBasicTagsFromIssue(modelIssueInfo);
            }
            
            // Store tags in state
            Console.WriteLine("\nStoring tags in state...");
            if (!State.IssueTags.ContainsKey(modelIssueInfo.Repository))
            {
                Console.WriteLine($"Creating new tag dictionary for repository {modelIssueInfo.Repository}");
                _logger.LogInformation("Creating new tag dictionary for repository {Repository}", modelIssueInfo.Repository);
                State.IssueTags[modelIssueInfo.Repository] = new Dictionary<string, List<string>>();
            }
            
            State.IssueTags[modelIssueInfo.Repository][modelIssueInfo.Id] = extractedTags.ToList();
            Console.WriteLine($"Stored {extractedTags.Length} tags for issue #{modelIssueInfo.Id}");
            _logger.LogInformation("Stored {Count} tags for issue #{IssueId} in repository {Repository}", 
                extractedTags.Length, modelIssueInfo.Id, modelIssueInfo.Repository);

            // Publish the extracted tags event
            try
            {
                Console.WriteLine("\nPublishing extracted tags event...");
                var tagsEvent = new IssueTagsEvent
                {
                    IssueId = modelIssueInfo.Id,
                    Title = modelIssueInfo.Title,
                    ExtractedTags = extractedTags,
                    Repository = modelIssueInfo.Repository
                };
                
                _logger.LogInformation("Publishing tags event for issue #{IssueId}", modelIssueInfo.Id);
                
                // Try to publish to both providers
                try
                {
                    Console.WriteLine("Attempting to publish with primary provider...");
                    var streamProvider = GetAppropriateStreamProvider();
                    var streamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey);
                    var stream = streamProvider.GetStream<IssueTagsEvent>(streamId);
                    await stream.OnNextAsync(tagsEvent);
                    
                    Console.WriteLine("Successfully published tags event with primary provider");
                    _logger.LogInformation("Successfully published tags event for issue #{IssueId}", modelIssueInfo.Id);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to publish tags with primary provider: {ex.Message}");
                    Console.WriteLine("Trying alternate provider...");
                    _logger.LogError(ex, "Failed to publish tags event with primary provider, trying alternate");
                    
                    try
                    {
                        // Try alternate provider
                        var streamProvider = this.GetStreamProvider("MemoryStreams");
                        var streamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey);
                        var stream = streamProvider.GetStream<IssueTagsEvent>(streamId);
                        await stream.OnNextAsync(tagsEvent);
                        
                        Console.WriteLine("Successfully published tags event with alternate provider");
                        _logger.LogInformation("Successfully published tags event for issue #{IssueId} with alternate provider", modelIssueInfo.Id);
                    }
                    catch (Exception innerEx)
                    {
                        Console.WriteLine($"Failed to publish tags with alternate provider: {innerEx.Message}");
                        _logger.LogError(innerEx, "Failed to publish tags event with alternate provider");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CRITICAL ERROR: Failed to publish tags event: {ex.Message}");
                _logger.LogError(ex, "CRITICAL ERROR: Failed to publish tags event. Details: {ExceptionMessage}", ex.Message);
            }

            // Generate summary report immediately for better feedback
            try
            {
                Console.WriteLine("\nGenerating summary report...");
                _logger.LogInformation("Generating summary report for repository: {Repository}", modelIssueInfo.Repository);
                await GenerateSummaryReportAsync(modelIssueInfo.Repository);
                Console.WriteLine("Successfully generated summary report");
                _logger.LogInformation("Successfully generated summary report for repository: {Repository}", modelIssueInfo.Repository);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to generate summary report: {ex.Message}");
                _logger.LogError(ex, "Failed to generate summary report for repository: {Repository}", modelIssueInfo.Repository);
            }
            
            Console.WriteLine("\n======== COMPLETED PROCESSING GITHUB ISSUE EVENT ========\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL ERROR in issue handling: {ex.Message}");
            _logger.LogError(ex, "FATAL ERROR in HandleGitHubIssueEventInternalAsync: {ExceptionMessage}", ex.Message);
        }
    }

    private async Task<string[]> ExtractTagsUsingLLMAsync(GrainInterfaces.Models.GitHubIssueInfo issueInfo)
    {
        try
        {
            // Add visible logging for LLM call
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss.fff}] ==========================================");
            Console.WriteLine($"EXTRACTING TAGS FOR ISSUE #{issueInfo.Id} USING LLM");
            Console.WriteLine($"===============================================\n");
            Console.ResetColor();
            
            _logger.LogInformation("Extracting tags for issue {IssueId} using LLM: {Title}", issueInfo.Id, issueInfo.Title);
            
            var tags = new List<string>();
            
            // Set up a timeout for the LLM API call
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45));
            var extractTask = Task.Run(async () => 
            {
                // Extract essential info to reduce token size
                string prompt = GenerateExtractTagsPrompt(issueInfo);
                
                Console.WriteLine($"Making LLM API call for tag extraction at {DateTime.Now:HH:mm:ss.fff}");
                string llmResponse = await _llmService.CompletePromptAsync(prompt);
                Console.WriteLine($"Completed LLM API call for tag extraction at {DateTime.Now:HH:mm:ss.fff}");
                
                return llmResponse;
            });
            
            // Wait for either the completion or the timeout
            var completedTask = await Task.WhenAny(extractTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("LLM API call for tag extraction timed out after 45 seconds for issue {IssueId}", issueInfo.Id);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"TAG EXTRACTION LLM CALL TIMED OUT - USING FALLBACK TAGS");
                Console.ResetColor();
                
                // Fall back to basic tag extraction
                return ExtractBasicTagsFromIssue(issueInfo);
            }
            
            // Get the result from the completed task
            string response = await extractTask;
            
            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("LLM response for tag extraction was empty for issue {IssueId}, using fallback", issueInfo.Id);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"EMPTY LLM RESPONSE - USING FALLBACK TAGS");
                Console.ResetColor();
                
                return ExtractBasicTagsFromIssue(issueInfo);
            }

            // Try to parse tags from response with different formats
            response = response.Trim();
            
            // Check for comma-separated list (most common format)
            if (response.Contains(",")) 
            {
                tags = response.Split(',').Select(t => t.Trim()).ToList();
            }
            // Check for line-by-line format
            else if (response.Contains('\n')) 
            {
                tags = response.Split('\n').Select(t => t.Trim()).ToList();
            }
            // If single tag or unrecognized format
            else 
            {
                tags.Add(response.Trim());
            }

            // Clean up tags, remove empty tags and limit count
            tags = tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Replace("`", "").Replace("#", "").Replace("*", "").Trim()) // Remove Markdown formatting
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct()
                .Take(10) // Limit to 10 tags max
                .ToList();
            
            // If we couldn't extract tags, fallback to basic extraction
            if (tags.Count == 0)
            {
                _logger.LogWarning("Failed to extract tags from LLM response for issue {IssueId}, using fallback", issueInfo.Id);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"NO VALID TAGS EXTRACTED - USING FALLBACK TAGS");
                Console.ResetColor();
                
                return ExtractBasicTagsFromIssue(issueInfo);
            }
            
            _logger.LogInformation("Successfully extracted {TagCount} tags for issue {IssueId} using LLM", 
                tags.Count, issueInfo.Id);
                
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"SUCCESSFULLY EXTRACTED {tags.Count} TAGS: {string.Join(", ", tags)}");
            Console.ResetColor();
            
            return tags.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting tags using LLM for issue {IssueId}, falling back to basic extraction", 
                issueInfo.Id);
            
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR EXTRACTING TAGS: {ex.Message}");
            Console.WriteLine("USING FALLBACK TAG EXTRACTION");
            Console.ResetColor();
            
            return ExtractBasicTagsFromIssue(issueInfo);
        }
    }

    private string[] ExtractBasicTagsFromIssue(GrainInterfaces.Models.GitHubIssueInfo issueInfo)
    {
        try
        {
            var basicTags = new List<string>();
            
            // Add any existing labels as tags
            if (issueInfo.Labels != null && issueInfo.Labels.Length > 0)
            {
                basicTags.AddRange(issueInfo.Labels);
            }
            
            // Add status as a tag
            if (!string.IsNullOrEmpty(issueInfo.Status))
            {
                basicTags.Add(issueInfo.Status.ToLower());
            }
            
            // Extract more contextual keywords based on issue content
            string titleAndDesc = $"{issueInfo.Title} {issueInfo.Description}".ToLower();
            
            // Security-related keywords
            if (titleAndDesc.Contains("security") || titleAndDesc.Contains("vulnerability") || 
                titleAndDesc.Contains("exploit") || titleAndDesc.Contains("hack") || 
                titleAndDesc.Contains("attack") || titleAndDesc.Contains("auth"))
            {
                basicTags.Add("security");
            }
            
            // Performance-related keywords
            if (titleAndDesc.Contains("slow") || titleAndDesc.Contains("performance") || 
                titleAndDesc.Contains("speed") || titleAndDesc.Contains("fast") || 
                titleAndDesc.Contains("optimize") || titleAndDesc.Contains("lag"))
            {
                basicTags.Add("performance");
            }
            
            // Bug-related keywords
            if (titleAndDesc.Contains("bug") || titleAndDesc.Contains("issue") || 
                titleAndDesc.Contains("problem") || titleAndDesc.Contains("crash") || 
                titleAndDesc.Contains("error") || titleAndDesc.Contains("fix") || 
                titleAndDesc.Contains("broken"))
            {
                basicTags.Add("bug");
            }
            
            // Feature-related keywords
            if (titleAndDesc.Contains("feature") || titleAndDesc.Contains("enhancement") || 
                titleAndDesc.Contains("add") || titleAndDesc.Contains("implement") || 
                titleAndDesc.Contains("new") || titleAndDesc.Contains("request"))
            {
                basicTags.Add("enhancement");
            }
            
            // Documentation-related keywords
            if (titleAndDesc.Contains("doc") || titleAndDesc.Contains("documentation") || 
                titleAndDesc.Contains("example") || titleAndDesc.Contains("readme") || 
                titleAndDesc.Contains("wiki"))
            {
                basicTags.Add("documentation");
            }
            
            // VPN-specific tags (since this is for a VPN repo)
            if (titleAndDesc.Contains("vpn") || titleAndDesc.Contains("connection") || 
                titleAndDesc.Contains("network") || titleAndDesc.Contains("connect") || 
                titleAndDesc.Contains("tunnel"))
            {
                basicTags.Add("vpn");
                basicTags.Add("networking");
            }
            
            // Authentication-related keywords
            if (titleAndDesc.Contains("login") || titleAndDesc.Contains("auth") || 
                titleAndDesc.Contains("sign in") || titleAndDesc.Contains("password") || 
                titleAndDesc.Contains("credential"))
            {
                basicTags.Add("authentication");
                basicTags.Add("user-account");
            }
            
            // Add default tags if we still don't have enough
            if (basicTags.Count < 3)
            {
                basicTags.Add("needs-triage");
                
                if (issueInfo.Status.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    basicTags.Add("resolved");
                }
                else
                {
                    basicTags.Add("open");
                    basicTags.Add("needs-investigation");
                }
            }
            
            return basicTags.Distinct().ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting tags from issue {IssueId}", issueInfo.Id);
            return new[] { "needs-triage", "extraction-error" };
        }
    }

    public async Task GenerateSummaryReportAsync(string repository)
    {
        try
        {
            Console.WriteLine($"\n\n======== GENERATING SUMMARY REPORT FOR {repository} ========");
            _logger.LogInformation("Generating summary report for repository: {Repository}", repository);
            
            if (!State.RepositoryIssues.ContainsKey(repository) || State.RepositoryIssues[repository].Count == 0)
            {
                Console.WriteLine($"ERROR: No issues found for repository {repository}, cannot generate summary report");
                _logger.LogWarning("No issues found for repository {Repository}, cannot generate summary report", repository);
                return;
            }

            // Get repository statistics
            var issues = State.RepositoryIssues[repository];
            var issueCount = issues.Count;
            var openIssues = issues.Count(i => i.State == "open");
            var closedIssues = issues.Count(i => i.State == "closed");
            var oldestIssueDate = issues.Min(i => i.CreatedAt);
            var newestIssueDate = issues.Max(i => i.CreatedAt);
            
            Console.WriteLine($"\nREPOSITORY STATS: {repository}");
            Console.WriteLine($"Total Issues: {issueCount} ({openIssues} open, {closedIssues} closed)");
            Console.WriteLine($"Date Range: {oldestIssueDate:yyyy-MM-dd} to {newestIssueDate:yyyy-MM-dd}");
            
            _logger.LogInformation("Repository {Repository} has {IssueCount} issues: {OpenCount} open, {ClosedCount} closed", 
                repository, issueCount, openIssues, closedIssues);

            // Get most common tags across all issues
            Dictionary<string, int> tagCounts = new Dictionary<string, int>();
            if (State.IssueTags.ContainsKey(repository))
            {
                foreach (var tagList in State.IssueTags[repository].Values)
                {
                    foreach (var tag in tagList)
                    {
                        if (!tagCounts.ContainsKey(tag))
                        {
                            tagCounts[tag] = 0;
                        }
                        tagCounts[tag]++;
                    }
                }
            }
            
            var topTags = tagCounts
                .OrderByDescending(t => t.Value)
                .Take(10)
                .Select(t => new TagStatistic { Tag = t.Key, Count = t.Value })
                .ToArray();
            
            Console.WriteLine("\nTOP TAGS:");
            foreach (var tag in topTags)
            {
                Console.WriteLine($"  - {tag.Tag}: {tag.Count} issues");
            }
            
            _logger.LogInformation("Extracted {Count} top tags for repository {Repository}", topTags.Length, repository);

            // Generate recommendations using LLM
            Console.WriteLine("\nGENERATING RECOMMENDATIONS...");
            var recommendations = await GetRecommendationsUsingLLMAsync(repository, 5);
            _logger.LogInformation("Generated {Count} recommendations for repository {Repository}", recommendations.Length, repository);

            // Calculate issue activity over time
            Console.WriteLine("\nGENERATING TIME-BASED STATISTICS...");
            var timeRanges = GenerateTimeRangeData(issues);
            _logger.LogInformation("Generated time-based statistics for {Count} periods", timeRanges.Length);
            
            Console.WriteLine("\nISSUE ACTIVITY OVER TIME:");
            foreach (var range in timeRanges)
            {
                Console.WriteLine($"  {range.StartDate:yyyy-MM-dd} to {range.EndDate:yyyy-MM-dd}: {range.IssuesCreated} created, {range.IssuesClosed} closed");
            }

            // Build the summary report
            var summaryReport = new RepositorySummaryReport
            {
                Repository = repository,
                GeneratedAt = DateTime.UtcNow,
                TotalIssues = issueCount,
                OpenIssues = openIssues,
                ClosedIssues = closedIssues,
                OldestIssueDate = oldestIssueDate,
                NewestIssueDate = newestIssueDate,
                TopTags = topTags,
                Recommendations = recommendations,
                TimeRanges = timeRanges
            };

            // Publish the summary report
            try
            {
                Console.WriteLine("\nPUBLISHING SUMMARY REPORT...");
                _logger.LogInformation("Publishing summary report for repository {Repository}", repository);
                
                try
                {
                    var streamProvider = GetAppropriateStreamProvider();
                    var streamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.SummaryStreamKey);
                    var stream = streamProvider.GetStream<RepositorySummaryReport>(streamId);
                    await stream.OnNextAsync(summaryReport);
                    
                    Console.WriteLine($"Successfully published summary report via {streamProvider} provider");
                    _logger.LogInformation("Successfully published summary report for repository {Repository}", repository);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to publish with primary provider: {ex.Message}, trying alternate");
                    _logger.LogError(ex, "Failed to publish summary report with primary provider, trying alternate");
                    
                    try
                    {
                        // Try alternate provider
                        var streamProvider = this.GetStreamProvider("MemoryStreams");
                        var streamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.SummaryStreamKey);
                        var stream = streamProvider.GetStream<RepositorySummaryReport>(streamId);
                        await stream.OnNextAsync(summaryReport);
                        
                        Console.WriteLine("Successfully published summary report with alternate provider");
                        _logger.LogInformation("Successfully published summary report with alternate provider");
                    }
                    catch (Exception innerEx)
                    {
                        Console.WriteLine($"Failed to publish with alternate provider: {innerEx.Message}, storing in state");
                        _logger.LogError(innerEx, "Failed to publish summary report with alternate provider");
                        
                        // Store in state as a last resort
                        if (!State.RepositorySummaries.ContainsKey(repository))
                        {
                            State.RepositorySummaries[repository] = new List<RepositorySummaryReport>();
                        }
                        
                        State.RepositorySummaries[repository].Add(summaryReport);
                        Console.WriteLine("Stored summary report in agent state");
                        _logger.LogInformation("Stored summary report in state for repository {Repository}", repository);
                    }
                }
                
                // Print a complete summary for the console
                Console.WriteLine("\n\n========== COMPLETE REPOSITORY ANALYSIS SUMMARY ==========");
                Console.WriteLine($"Repository: {repository}");
                Console.WriteLine($"Total Issues: {issueCount} ({openIssues} open, {closedIssues} closed)");
                Console.WriteLine($"Analysis Time: {summaryReport.GeneratedAt}");
                
                Console.WriteLine("\nTOP TAGS:");
                foreach (var tag in topTags)
                {
                    Console.WriteLine($"  - {tag.Tag}: {tag.Count} issues");
                }
                
                Console.WriteLine("\nRECOMMENDATIONS:");
                foreach (var rec in recommendations)
                {
                    Console.WriteLine($"\n* {rec.Title} (Priority: {rec.Priority})");
                    Console.WriteLine($"  Supporting Issues: {string.Join(", ", rec.SupportingIssues.Select(i => $"#{i}"))}");
                    Console.WriteLine($"  {rec.Description}");
                }
                
                Console.WriteLine("\n========== END OF ANALYSIS SUMMARY ==========\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CRITICAL ERROR publishing summary report: {ex.Message}");
                _logger.LogError(ex, "CRITICAL ERROR: Failed to publish summary report. Details: {ExceptionMessage}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR generating summary report: {ex.Message}");
            _logger.LogError(ex, "Failed to generate summary report for repository {Repository}", repository);
        }
    }

    private TimeRangeStatistic[] GenerateTimeRangeData(List<GrainInterfaces.Models.GitHubIssueInfo> issues)
    {
        try
        {
            _logger.LogInformation("Generating time-based statistics for {Count} issues", issues.Count);
            
            if (issues.Count == 0)
            {
                return Array.Empty<TimeRangeStatistic>();
            }

            // Determine the time ranges to use based on the data
            DateTime oldestDate = issues.Min(i => i.CreatedAt);
            DateTime newestDate = DateTime.UtcNow;
            TimeSpan totalTimeSpan = newestDate - oldestDate;
            
            List<TimeRangeStatistic> statistics = new List<TimeRangeStatistic>();
            
            // If less than 30 days of data, do daily stats for the past week
            if (totalTimeSpan.TotalDays < 30)
            {
                _logger.LogInformation("Using daily statistics for the past week");
                
                for (int i = 6; i >= 0; i--)
                {
                    DateTime day = DateTime.UtcNow.Date.AddDays(-i);
                    DateTime nextDay = day.AddDays(1);
                    
                    int issuesCreated = issues.Count(issue => issue.CreatedAt >= day && issue.CreatedAt < nextDay);
                    int issuesClosed = issues.Count(issue => 
                        issue.State == "closed" && 
                        issue.ClosedAt.HasValue && 
                        issue.ClosedAt.Value >= day && 
                        issue.ClosedAt.Value < nextDay);
                    
                    statistics.Add(new TimeRangeStatistic
                    {
                        StartDate = day,
                        EndDate = nextDay,
                        IssuesCreated = issuesCreated,
                        IssuesClosed = issuesClosed
                    });
                }
            }
            // If less than 90 days, do weekly stats
            else if (totalTimeSpan.TotalDays < 90)
            {
                _logger.LogInformation("Using weekly statistics for the past 4 weeks");
                
                for (int i = 3; i >= 0; i--)
                {
                    DateTime weekStart = DateTime.UtcNow.Date.AddDays(-(i * 7) - 6);
                    DateTime weekEnd = weekStart.AddDays(7);
                    
                    int issuesCreated = issues.Count(issue => issue.CreatedAt >= weekStart && issue.CreatedAt < weekEnd);
                    int issuesClosed = issues.Count(issue => 
                        issue.State == "closed" && 
                        issue.ClosedAt.HasValue && 
                        issue.ClosedAt.Value >= weekStart && 
                        issue.ClosedAt.Value < weekEnd);
                    
                    statistics.Add(new TimeRangeStatistic
                    {
                        StartDate = weekStart,
                        EndDate = weekEnd,
                        IssuesCreated = issuesCreated,
                        IssuesClosed = issuesClosed
                    });
                }
            }
            // Otherwise, do monthly stats
            else
            {
                _logger.LogInformation("Using monthly statistics for the past 6 months");
                
                for (int i = 5; i >= 0; i--)
                {
                    DateTime monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-i);
                    DateTime monthEnd = monthStart.AddMonths(1);
                    
                    int issuesCreated = issues.Count(issue => issue.CreatedAt >= monthStart && issue.CreatedAt < monthEnd);
                    int issuesClosed = issues.Count(issue => 
                        issue.State == "closed" && 
                        issue.ClosedAt.HasValue && 
                        issue.ClosedAt.Value >= monthStart && 
                        issue.ClosedAt.Value < monthEnd);
                    
                    statistics.Add(new TimeRangeStatistic
                    {
                        StartDate = monthStart,
                        EndDate = monthEnd,
                        IssuesCreated = issuesCreated,
                        IssuesClosed = issuesClosed
                    });
                }
            }
            
            return statistics.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate time range statistics");
            return Array.Empty<TimeRangeStatistic>();
        }
    }

    private async Task<IssueRecommendation[]> GetRecommendationsUsingLLMAsync(string repository, int topIssuesCount = 3)
    {
        try
        {
            // Add visible logging for LLM call
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss.fff}] ==========================================");
            Console.WriteLine($"GENERATING RECOMMENDATIONS FOR REPOSITORY {repository} USING LLM");
            Console.WriteLine($"===============================================\n");
            Console.ResetColor();
            
            _logger.LogInformation("Getting recommendations for repository {Repository} using LLM");
            
            // Get all issues for the repository
            var issues = await GetIssuesForRepositoryAsync(repository);
            if (issues == null || issues.Count == 0)
            {
                _logger.LogWarning("No issues found for repository {Repository}, cannot generate recommendations", repository);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"NO ISSUES FOUND - USING FALLBACK RECOMMENDATIONS");
                Console.ResetColor();
                
                return CreateBasicRecommendations(repository, new List<GrainInterfaces.Models.GitHubIssueInfo>());
            }
            
            // Take the most recent issues for analysis
            var recentIssues = issues
                .OrderByDescending(i => i.CreatedAt)
                .Take(Math.Min(issues.Count, 20)) // Limit to 20 most recent issues
                .ToList();

            // Generate prompt for recommendations
            string prompt = GenerateRecommendationsPrompt(repository, recentIssues, topIssuesCount);
            
            // Set up a timeout for the LLM API call
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60)); // Longer timeout for recommendations
            var recommendTask = Task.Run(async () => 
            {
                Console.WriteLine($"Making LLM API call for recommendations at {DateTime.Now:HH:mm:ss.fff}");
                string llmResponse = await _llmService.CompletePromptAsync(prompt);
                Console.WriteLine($"Completed LLM API call for recommendations at {DateTime.Now:HH:mm:ss.fff}");
                
                return llmResponse;
            });
            
            // Wait for either the completion or the timeout
            var completedTask = await Task.WhenAny(recommendTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("LLM API call for recommendations timed out after 60 seconds for repository {Repository}", repository);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"RECOMMENDATIONS LLM CALL TIMED OUT - USING FALLBACK RECOMMENDATIONS");
                Console.ResetColor();
                
                // Fall back to basic recommendations
                return CreateBasicRecommendations(repository, recentIssues);
            }
            
            // Get the result from the completed task
            string response = await recommendTask;
            
            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("LLM response for recommendations was empty for repository {Repository}, using fallback", repository);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"EMPTY LLM RESPONSE - USING FALLBACK RECOMMENDATIONS");
                Console.ResetColor();
                
                return CreateBasicRecommendations(repository, recentIssues);
            }
            
            // Try to parse recommendations from the response
            var recommendations = ParseRecommendationsFromLLMResponse(response, recentIssues);
            
            if (recommendations == null || recommendations.Length == 0)
            {
                _logger.LogWarning("Failed to parse recommendations from LLM response for repository {Repository}, using fallback", 
                    repository);
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"NO VALID RECOMMENDATIONS PARSED - USING FALLBACK RECOMMENDATIONS");
                Console.ResetColor();
                
                return CreateBasicRecommendations(repository, recentIssues);
            }
            
            _logger.LogInformation("Successfully generated {Count} recommendations for repository {Repository} using LLM", 
                recommendations.Length, repository);
                
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"SUCCESSFULLY GENERATED {recommendations.Length} RECOMMENDATIONS");
            Console.ResetColor();
            
            return recommendations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recommendations using LLM for repository {Repository}, falling back to basic recommendations", 
                repository);
            
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR GENERATING RECOMMENDATIONS: {ex.Message}");
            Console.WriteLine("USING FALLBACK RECOMMENDATION GENERATION");
            Console.ResetColor();
            
            // Fall back to basic recommendations
            var issues = await GetIssuesForRepositoryAsync(repository);
            var recentIssues = (issues ?? new List<GrainInterfaces.Models.GitHubIssueInfo>())
                .OrderByDescending(i => i.CreatedAt)
                .Take(Math.Min(issues?.Count ?? 0, 20))
                .ToList();
            
            return CreateBasicRecommendations(repository, recentIssues);
        }
    }

    // Helper method to create basic recommendations when LLM fails
    private IssueRecommendation[] CreateBasicRecommendations(string repository, List<GrainInterfaces.Models.GitHubIssueInfo> recentIssues)
    {
        try
        {
            Console.WriteLine("Creating basic recommendations based on issue content");
            _logger.LogInformation("Creating basic recommendations for repository {Repository}", repository);
            
            var recommendations = new List<IssueRecommendation>();
            
            // Check if there are open issues
            var openIssues = recentIssues.Where(i => i.State == "open").ToList();
            if (openIssues.Any())
            {
                recommendations.Add(new IssueRecommendation
                {
                    Title = "Address Open Issues",
                    Description = $"There are {openIssues.Count} open issues that need attention. Prioritize addressing these open issues to improve user experience and maintain repository health.",
                    Priority = Priority.High,
                    SupportingIssues = openIssues.Select(i => i.Id).ToArray()
                });
            }
            
            // Check for feature requests
            var featureIssues = recentIssues.Where(i => 
                (i.Title?.Contains("feature", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Description?.Contains("feature", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Title?.Contains("add", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Title?.Contains("implement", StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
            
            if (featureIssues.Any())
            {
                recommendations.Add(new IssueRecommendation
                {
                    Title = "Evaluate Feature Requests",
                    Description = $"There are {featureIssues.Count} issues that appear to be feature requests. Review these requests to determine which would provide the most value to users and prioritize their implementation.",
                    Priority = Priority.Medium,
                    SupportingIssues = featureIssues.Select(i => i.Id).ToArray()
                });
            }
            
            // Check for potential bugs
            var bugIssues = recentIssues.Where(i => 
                (i.Title?.Contains("bug", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Description?.Contains("bug", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Title?.Contains("error", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Title?.Contains("fix", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Title?.Contains("crash", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Title?.Contains("problem", StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
            
            if (bugIssues.Any())
            {
                recommendations.Add(new IssueRecommendation
                {
                    Title = "Fix Reported Bugs",
                    Description = $"There are {bugIssues.Count} issues that appear to be bug reports. Investigate and fix these issues to improve stability and user satisfaction.",
                    Priority = Priority.High,
                    SupportingIssues = bugIssues.Select(i => i.Id).ToArray()
                });
            }
            
            // Add a general recommendation if we don't have enough
            if (recommendations.Count < 3)
            {
                recommendations.Add(new IssueRecommendation
                {
                    Title = "Review Recent Repository Activity",
                    Description = "Review the most recent issues to identify common themes and user needs. Regular review of repository activity helps maintain quality and ensures user concerns are addressed promptly.",
                    Priority = Priority.Medium,
                    SupportingIssues = recentIssues.Select(i => i.Id).ToArray()
                });
            }
            
            return recommendations.Take(3).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating basic recommendations for repository {Repository}", repository);
            
            // Return a single failsafe recommendation
            return new[] 
            {
                new IssueRecommendation
                {
                    Title = "Review Repository Issues",
                    Description = "We recommend manually reviewing the repository issues to identify patterns and prioritize work.",
                    Priority = Priority.Medium,
                    SupportingIssues = recentIssues.Select(i => i.Id).ToArray()
                }
            };
        }
    }

    private IssueRecommendation[] ParseRecommendationsFromLLMResponse(string llmResponse, List<GrainInterfaces.Models.GitHubIssueInfo> availableIssues)
    {
        try
        {
            var recommendations = new List<IssueRecommendation>();
            
            // Split the response into recommendation blocks
            var recommendationPattern = @"RECOMMENDATION\s+\d+:[\s\S]*?(?=RECOMMENDATION\s+\d+:|$)";
            var matches = Regex.Matches(llmResponse, recommendationPattern, RegexOptions.IgnoreCase);
            
            if (matches.Count == 0)
            {
                _logger.LogWarning("Could not find recommendation pattern in LLM response, trying alternative parsing");
                
                // Try to extract recommendations by looking for title pattern
                var titlePattern = @"Title:\s*([^\n]+)";
                var titleMatches = Regex.Matches(llmResponse, titlePattern, RegexOptions.IgnoreCase);
                
                if (titleMatches.Count > 0)
                {
                    _logger.LogInformation("Found {Count} title patterns, attempting to parse recommendations", titleMatches.Count);
                    var sections = llmResponse.Split(new[] { "Title:" }, StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var section in sections.Skip(1))  // Skip the first split which is before any "Title:"
                    {
                        try
                        {
                            var fullSection = "Title:" + section;
                            
                            var title = Regex.Match(fullSection, @"Title:\s*([^\n]+)", RegexOptions.IgnoreCase)
                                .Groups[1].Value.Trim();
                                
                            var priority = Regex.Match(fullSection, @"Priority:\s*([^\n]+)", RegexOptions.IgnoreCase)
                                .Groups[1].Value.Trim();
                                
                            var description = Regex.Match(fullSection, @"Description:\s*([\s\S]*?)(?=Supporting Issues:|Priority:|Title:|$)", RegexOptions.IgnoreCase)
                                .Groups[1].Value.Trim();
                                
                            var supportingIssuesText = Regex.Match(fullSection, @"Supporting Issues:\s*([\s\S]*?)(?=RECOMMENDATION|$)", RegexOptions.IgnoreCase)
                                .Groups[1].Value.Trim();
                            
                            var supportingIssues = new List<string>();
                            var issueNumberPattern = @"#(\d+)";
                            foreach (Match issueMatch in Regex.Matches(supportingIssuesText, issueNumberPattern))
                            {
                                supportingIssues.Add(issueMatch.Groups[1].Value);
                            }
                            
                            if (!string.IsNullOrEmpty(title))
                            {
                                var recommendation = new IssueRecommendation
                                {
                                    Title = title,
                                    Description = description,
                                    Priority = GetPriorityEnum(priority),
                                    SupportingIssues = supportingIssues.ToArray()
                                };
                                
                                recommendations.Add(recommendation);
                                _logger.LogInformation("Successfully parsed recommendation: {Title}", title);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse recommendation section");
                        }
                    }
                }
                
                if (recommendations.Count == 0)
                {
                    _logger.LogWarning("Failed to parse any recommendations, constructing fallback recommendation");
                    
                    // Create a fallback recommendation
                    recommendations.Add(new IssueRecommendation
                    {
                        Title = "Review Recent Repository Issues",
                        Description = "Multiple issues were detected in the repository but the system couldn't generate specific recommendations. Please review the most recent issues manually.",
                        Priority = Priority.Medium,
                        SupportingIssues = availableIssues.Select(i => i.Id).ToArray()
                    });
                }
                
                return recommendations.ToArray();
            }
            
            // Standard parsing from well-formatted response
            foreach (Match match in matches)
            {
                try
                {
                    var recommendationText = match.Value;
                    
                    var title = Regex.Match(recommendationText, @"Title:\s*([^\n]+)")
                        .Groups[1].Value.Trim();
                        
                    var priority = Regex.Match(recommendationText, @"Priority:\s*([^\n]+)")
                        .Groups[1].Value.Trim();
                        
                    var description = Regex.Match(recommendationText, @"Description:\s*([\s\S]*?)(?=Supporting Issues:|$)")
                        .Groups[1].Value.Trim();
                        
                    var supportingIssuesText = Regex.Match(recommendationText, @"Supporting Issues:\s*([\s\S]*)")
                        .Groups[1].Value.Trim();
                    
                    var supportingIssues = new List<string>();
                    var issueNumberPattern = @"#(\d+)";
                    foreach (Match issueMatch in Regex.Matches(supportingIssuesText, issueNumberPattern))
                    {
                        supportingIssues.Add(issueMatch.Groups[1].Value);
                    }
                    
                    var recommendation = new IssueRecommendation
                    {
                        Title = title,
                        Description = description,
                        Priority = GetPriorityEnum(priority),
                        SupportingIssues = supportingIssues.ToArray()
                    };
                    
                    recommendations.Add(recommendation);
                    _logger.LogInformation("Successfully parsed recommendation: {Title}", title);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse recommendation from match");
                }
            }
            
            return recommendations.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing recommendations from LLM response");
            return Array.Empty<IssueRecommendation>();
        }
    }

    private Priority GetPriorityEnum(string priority)
    {
        if (string.IsNullOrEmpty(priority))
            return Priority.Medium;
        
        return priority.Trim().ToLower() switch
        {
            "high" => Priority.High,
            "low" => Priority.Low,
            _ => Priority.Medium
        };
    }

    // Add the required IAsyncObserver implementation methods
    public Task OnCompletedAsync()
    {
        _logger.LogInformation("Stream completed notification received");
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        _logger.LogError(ex, "Stream error notification received");
        return Task.CompletedTask;
    }

    // Helper method to generate a structured prompt for tag extraction
    private string GenerateExtractTagsPrompt(GrainInterfaces.Models.GitHubIssueInfo issueInfo)
    {
        return $@"Analyze the following GitHub issue and extract a list of tags/themes (5-10 tags).
The tags should categorize the issue and identify key themes that could help development teams prioritize their work.
Focus on extracting technical concepts, feature areas, priorities, and issue types.

GITHUB ISSUE:
ID: {issueInfo.Id}
Title: {issueInfo.Title}
Repository: {issueInfo.Repository}
Status: {issueInfo.Status}
State: {issueInfo.State}

Description:
{issueInfo.Description?.Substring(0, Math.Min(issueInfo.Description?.Length ?? 0, 2000)) ?? "(No description provided)"}

Existing labels: {(issueInfo.Labels != null && issueInfo.Labels.Length > 0 ? string.Join(", ", issueInfo.Labels) : "None")}

OUTPUT INSTRUCTIONS:
1. Return ONLY a comma-separated list of tags (maximum 10 tags).
2. Do not include any explanations, headers, or additional text.
3. Each tag should be a single word or short phrase (1-3 words).
4. Include both technical tags and priority/category tags.
5. Format examples: bug, enhancement, documentation, high-priority, ui, api, performance, security, etc.

Extract and return these tags as a simple comma-separated list:";
    }

    // Helper method to generate a structured prompt for repository recommendations
    private string GenerateRecommendationsPrompt(string repository, List<GrainInterfaces.Models.GitHubIssueInfo> recentIssues, int topIssuesCount)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine($@"Analyze the following GitHub issues from the repository '{repository}' and generate {topIssuesCount} prioritized recommendations for the development team.

REPOSITORY: {repository}
TOTAL ISSUES: {recentIssues.Count}

ISSUES TO ANALYZE:");

        // Include at most 15 issues to keep the prompt size reasonable
        foreach (var issue in recentIssues.Take(15))
        {
            sb.AppendLine($@"
Issue #{issue.Id}
Title: {issue.Title}
Status: {issue.State}
Created: {issue.CreatedAt:yyyy-MM-dd}
Labels: {(issue.Labels != null && issue.Labels.Length > 0 ? string.Join(", ", issue.Labels) : "None")}
Description Summary: {issue.Description?.Substring(0, Math.Min(issue.Description?.Length ?? 0, 300)) ?? "(No description provided)"}
");
        }

        sb.AppendLine($@"
Based on the issues above, generate {topIssuesCount} clear recommendations for the development team.
Each recommendation should:
1. Address common themes or important issues
2. Have a clear, actionable title
3. Include a priority level (High, Medium, or Low)
4. Provide a concise explanation
5. Reference supporting issue IDs

FORMAT YOUR RESPONSE AS FOLLOWS:

RECOMMENDATION 1:
Title: [Clear, actionable title]
Priority: [High/Medium/Low]
Description: [2-3 sentence explanation of the recommendation]
Supporting Issues: [List issue IDs that support this recommendation]

RECOMMENDATION 2:
Title: [Clear, actionable title]
Priority: [High/Medium/Low]
Description: [2-3 sentence explanation of the recommendation]
Supporting Issues: [List issue IDs that support this recommendation]

... and so on for {topIssuesCount} recommendations.

Focus on providing valuable insights that will help the development team prioritize their work and address the most important issues in the repository.
");

        return sb.ToString();
    }

    // Helper method to get issues for a repository
    private Task<List<GrainInterfaces.Models.GitHubIssueInfo>> GetIssuesForRepositoryAsync(string repository)
    {
        List<GrainInterfaces.Models.GitHubIssueInfo> issues = new List<GrainInterfaces.Models.GitHubIssueInfo>();
        
        if (State.RepositoryIssues.ContainsKey(repository) && State.RepositoryIssues[repository].Count > 0)
        {
            issues = State.RepositoryIssues[repository];
            _logger.LogInformation("Found {Count} issues for repository {Repository} in state", 
                issues.Count, repository);
        }
        else
        {
            _logger.LogWarning("No issues found for repository {Repository} in state", repository);
        }
        
        return Task.FromResult(issues);
    }
}

// Fix the interface to not duplicate IAsyncObserver methods
public interface IGitHubAnalysisGAgent : IGAgent, IAsyncObserver<GitHubIssueEvent>
{
    Task HandleGitHubIssueEventAsync(GitHubIssueEvent @event);
    Task GenerateSummaryReportAsync(string repository);
} 