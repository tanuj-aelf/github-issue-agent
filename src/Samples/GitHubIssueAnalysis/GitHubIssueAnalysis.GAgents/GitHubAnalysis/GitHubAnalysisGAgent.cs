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

// Fix the ImplicitStreamSubscription to use the static StreamNamespace
[ImplicitStreamSubscription(GitHubAnalysisStream.StreamNamespace)]
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
                
                Console.WriteLine("\n========== RECEIVED TAGS EVENT ==========");
                Console.WriteLine($"Repository: {item.Repository}");
                Console.WriteLine($"Issue: #{item.IssueId} - {item.Title}");
                Console.WriteLine($"Extraction Time: {DateTime.UtcNow}");
                
                Console.WriteLine("\nEXTRACTED TAGS:");
                if (item.ExtractedTags != null && item.ExtractedTags.Length > 0)
                {
                    foreach (var tag in item.ExtractedTags)
                    {
                        Console.WriteLine($"  - {tag}");
                    }
                    Console.WriteLine($"\nTotal Tags: {item.ExtractedTags.Length}");
                }
                else
                {
                    Console.WriteLine("  No tags extracted");
                }
                
                Console.WriteLine("========== END OF TAGS EVENT ==========\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in TagsStreamObserver: {ex.Message}");
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
                Console.WriteLine("\n\n========== RECEIVED REPOSITORY ANALYSIS ==========");
                Console.WriteLine($"Repository: {item.Repository}");
                Console.WriteLine($"Total Issues: {item.TotalIssues} ({item.OpenIssues} open, {item.ClosedIssues} closed)");
                Console.WriteLine($"Generated: {item.GeneratedAt}");
                
                Console.WriteLine("\nTOP TAGS:");
                if (item.TopTags != null && item.TopTags.Length > 0)
                {
                    foreach (var tag in item.TopTags)
                    {
                        Console.WriteLine($"  - {tag.Tag}: {tag.Count} issue(s)");
                    }
                }
                else
                {
                    Console.WriteLine("  No tags found");
                }
                
                Console.WriteLine("\nRECOMMENDATIONS:");
                if (item.Recommendations != null && item.Recommendations.Length > 0)
                {
                    foreach (var rec in item.Recommendations)
                    {
                        Console.WriteLine($"\n* {rec.Title} (Priority: {rec.Priority})");
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
                    Console.WriteLine("  No recommendations generated");
                }
                
                Console.WriteLine("\nISSUE ACTIVITY:");
                if (item.TimeRanges != null && item.TimeRanges.Length > 0)
                {
                    foreach (var range in item.TimeRanges)
                    {
                        Console.WriteLine($"  {range.StartDate:yyyy-MM-dd} to {range.EndDate:yyyy-MM-dd}: {range.IssuesCreated} created, {range.IssuesClosed} closed");
                    }
                }
                else
                {
                    Console.WriteLine("  No activity data available");
                }
                
                Console.WriteLine("\n========== END OF REPOSITORY ANALYSIS ==========\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in SummaryStreamObserver: {ex.Message}");
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
            Console.WriteLine("\n\n********************************************************************");
            Console.WriteLine("*                   RECEIVED GITHUB ISSUE EVENT                     *");
            Console.WriteLine("********************************************************************");
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
            Console.WriteLine("\nBEGINNING ISSUE ANALYSIS - THIS MAY TAKE UP TO 30 SECONDS\n");
            await HandleGitHubIssueEventInternalAsync(@event);
            
            // Force summary generation after each event for immediate feedback
            try
            {
                string repository = @event.IssueInfo.Repository;
                Console.WriteLine("\nFORCING SUMMARY GENERATION FOR IMMEDIATE FEEDBACK...");
                _logger.LogWarning("Forcing summary generation after receiving event for repository: {Repository}", repository);
                
                // Wait a moment to ensure tags have been processed
                await Task.Delay(1000);
                
                // Generate summary report
                await GenerateSummaryReportAsync(repository);
                
                Console.WriteLine("\nSUMMARY REPORT GENERATED AND PUBLISHED TO STREAM\n");
                _logger.LogWarning("Summary report generated and should be published to stream");
                
                // Output clear completion message
                Console.WriteLine("\n********************************************************************");
                Console.WriteLine("*               COMPLETED GITHUB ISSUE ANALYSIS                    *");
                Console.WriteLine("*                                                                  *");
                Console.WriteLine("*        Results have been published to the summary stream         *");
                Console.WriteLine("*     You should see the detailed analysis output above/below      *");
                Console.WriteLine("********************************************************************\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nERROR IN SUMMARY GENERATION: {ex.Message}");
                _logger.LogError(ex, "Error generating forced summary after event processing");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nCRITICAL ERROR IN EVENT HANDLER: {ex.Message}");
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
            Console.WriteLine($"\n\n======== EXTRACTING TAGS FOR ISSUE #{issueInfo.Id}: {issueInfo.Title} ========");
            _logger.LogWarning("========== STARTING LLM CALL FOR TAG EXTRACTION ==========");
            _logger.LogWarning("Processing issue: {IssueTitle} (#{IssueId})", issueInfo.Title, issueInfo.Id);
            
            // Create a more effective prompt for better tag extraction
            string prompt = $@"
You are analyzing a GitHub issue to extract relevant tags. The tags will be used to categorize issues and identify common themes.

ISSUE DETAILS:
ID: {issueInfo.Id}
Title: {issueInfo.Title}
Description: {issueInfo.Description}
Status: {issueInfo.Status}
Repository: {issueInfo.Repository}
Existing Labels: {string.Join(", ", issueInfo.Labels)}

TASK:
Extract 5-8 most relevant tags from this issue that describe:
1. Issue type (bug, feature-request, question, documentation, etc.)
2. Technical areas (networking, ui, database, authentication, etc.)
3. Priority level (critical, high, medium, low)
4. Affected components (if identifiable)

FORMAT REQUIREMENTS:
- Return ONLY a comma-separated list of tags (no explanations)
- Use lowercase with hyphens for multi-word tags (e.g. 'feature-request')
- Be specific and descriptive
- Include the issue status (open/closed) as one of the tags

EXAMPLE OUTPUT:
bug, networking, authentication, high-priority, connection-error, open
";

            Console.WriteLine("Sending LLM prompt for tag extraction...");
            _logger.LogInformation("Calling LLM service for tag extraction");
            var tags = await _llmService.CompletePromptAsync(prompt);
            
            Console.WriteLine($"LLM RESPONSE FOR TAGS: {tags}");
            _logger.LogWarning("LLM Response for tag extraction: {Response}", tags);
            
            if (string.IsNullOrWhiteSpace(tags))
            {
                Console.WriteLine("WARNING: LLM returned empty tags, trying simpler prompt");
                _logger.LogWarning("LLM returned empty tags for issue {IssueId}, trying simpler prompt", issueInfo.Id);
                
                // Try a simpler prompt as fallback
                string simplePrompt = $@"
Extract 5-8 tags from this GitHub issue:
Title: {issueInfo.Title}
Description: {issueInfo.Description}
Status: {issueInfo.Status}

Return only comma-separated tags.
";
                Console.WriteLine("Trying fallback prompt for tag extraction...");
                tags = await _llmService.CompletePromptAsync(simplePrompt);
                Console.WriteLine($"FALLBACK LLM RESPONSE: {tags}");
                
                if (string.IsNullOrWhiteSpace(tags))
                {
                    Console.WriteLine("WARNING: LLM still returned empty tags, falling back to basic extraction");
                    _logger.LogWarning("LLM still returned empty tags with simpler prompt, falling back to basic extraction");
                    return ExtractBasicTagsFromIssue(issueInfo);
                }
            }
            
            var tagArray = tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .ToArray();
                
            if (tagArray.Length == 0)
            {
                Console.WriteLine("WARNING: No valid tags found, using basic extraction");
                _logger.LogWarning("No valid tags found after processing LLM response, falling back to basic extraction");
                return ExtractBasicTagsFromIssue(issueInfo);
            }
            
            Console.WriteLine($"EXTRACTED TAGS ({tagArray.Length}): {string.Join(", ", tagArray)}");
            _logger.LogWarning("Successfully extracted {Count} tags for issue #{IssueId}: {Tags}", 
                tagArray.Length, issueInfo.Id, string.Join(", ", tagArray));
                
            return tagArray;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: Tag extraction failed: {ex.Message}");
            _logger.LogError(ex, "Error extracting tags using LLM for issue {IssueId}, falling back to basic extraction", issueInfo.Id);
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

    private async Task GenerateSummaryReportAsync(string repository)
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
            Console.WriteLine($"\n\n======== GENERATING RECOMMENDATIONS FOR REPOSITORY: {repository} ========");
            _logger.LogInformation("Generating recommendations for repository {Repository}, considering top {Count} issues", 
                repository, topIssuesCount);
            
            if (!State.RepositoryIssues.ContainsKey(repository) || State.RepositoryIssues[repository].Count == 0)
            {
                Console.WriteLine($"ERROR: No issues found for repository {repository}");
                _logger.LogWarning("No issues found for repository {Repository}, cannot generate recommendations", repository);
                return Array.Empty<IssueRecommendation>();
            }

            // Get the most recent issues, sorted by creation date
            var recentIssues = State.RepositoryIssues[repository]
                .OrderByDescending(i => i.CreatedAt)
                .Take(topIssuesCount)
                .ToList();
            
            Console.WriteLine($"Found {recentIssues.Count} recent issues to analyze for recommendations");
            
            if (recentIssues.Count == 0)
            {
                Console.WriteLine($"ERROR: No recent issues found for repository {repository}");
                _logger.LogWarning("No recent issues found for repository {Repository}, cannot generate recommendations", repository);
                return Array.Empty<IssueRecommendation>();
            }

            // Extract tags for all these issues
            var issuesWithTags = new List<(GrainInterfaces.Models.GitHubIssueInfo Issue, List<string> Tags)>();
            foreach (var issue in recentIssues)
            {
                if (State.IssueTags.ContainsKey(repository) && 
                    State.IssueTags[repository].ContainsKey(issue.Id))
                {
                    Console.WriteLine($"Using existing tags for issue #{issue.Id}");
                    issuesWithTags.Add((issue, State.IssueTags[repository][issue.Id]));
                }
                else
                {
                    // If tags haven't been extracted yet, do it now
                    try
                    {
                        Console.WriteLine($"Tags not found for issue #{issue.Id}, extracting now");
                        _logger.LogInformation("Tags not found for issue #{IssueId}, extracting now", issue.Id);
                        var tags = await ExtractTagsUsingLLMAsync(issue);
                        issuesWithTags.Add((issue, tags.ToList()));
                        
                        // Store for future use
                        if (!State.IssueTags.ContainsKey(repository))
                        {
                            State.IssueTags[repository] = new Dictionary<string, List<string>>();
                        }
                        State.IssueTags[repository][issue.Id] = tags.ToList();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR: Failed to extract tags for issue #{issue.Id}: {ex.Message}");
                        _logger.LogError(ex, "Failed to extract tags for issue #{IssueId}, using empty tag list", issue.Id);
                        issuesWithTags.Add((issue, new List<string>()));
                    }
                }
            }

            Console.WriteLine("Preparing issue summaries for LLM prompt...");
            var issuesText = string.Join("\n\n", issuesWithTags.Select(it => 
                $"Issue #{it.Issue.Id}: {it.Issue.Title}\n" +
                $"Created: {it.Issue.CreatedAt}\n" +
                $"Description: {it.Issue.Description?.Substring(0, Math.Min(it.Issue.Description?.Length ?? 0, 500)) ?? "No description"}\n" +
                $"Tags: {string.Join(", ", it.Tags)}"
            ));

            // Build a comprehensive prompt for the LLM
            var prompt = $@"
I need you to analyze GitHub issues from the repository '{repository}' and provide actionable recommendations.

Here are the most recent issues:

{issuesText}

Based on these issues, please provide THREE specific, actionable recommendations for the repository maintainers.
For each recommendation:
1. Provide a clear, concise title (max 100 characters)
2. Write a detailed description explaining the reasoning behind the recommendation (100-300 words)
3. List any specific issues that support this recommendation
4. Assign a priority level (High, Medium, Low) based on urgency and impact

FORMAT YOUR RESPONSE EXACTLY AS FOLLOWS:
```
RECOMMENDATION 1:
Title: [Short descriptive title]
Priority: [High/Medium/Low]
Description: [Detailed explanation]
Supporting Issues: [List of issue numbers]

RECOMMENDATION 2:
Title: [Short descriptive title]
Priority: [High/Medium/Low]
Description: [Detailed explanation]
Supporting Issues: [List of issue numbers]

RECOMMENDATION 3:
Title: [Short descriptive title]
Priority: [High/Medium/Low]
Description: [Detailed explanation]
Supporting Issues: [List of issue numbers]
```

IMPORTANT: Ensure your recommendations are SPECIFIC and ACTIONABLE. Do not provide generic advice.
";

            Console.WriteLine("Sending LLM prompt for recommendations...");
            _logger.LogInformation("Calling LLM for repository recommendations analysis");
            var llmResponse = await _llmService.CompletePromptAsync(prompt);
            Console.WriteLine($"\nRECEIVED LLM RESPONSE OF LENGTH: {llmResponse?.Length ?? 0} CHARACTERS");
            _logger.LogInformation("Received LLM response for recommendations, processing");

            if (string.IsNullOrEmpty(llmResponse))
            {
                Console.WriteLine("WARNING: Received empty response from LLM, trying simpler prompt");
                _logger.LogWarning("Received empty response from LLM for recommendations, trying simplified prompt");
                
                // Simplified fallback prompt
                var fallbackPrompt = $@"
Analyze these GitHub issues and provide 3 actionable recommendations:

{issuesText}

Format: For each recommendation, include:
1. Title (one line)
2. Priority (High/Medium/Low)
3. Description (brief paragraph)
4. Supporting Issues (list of numbers)
";
                Console.WriteLine("Sending fallback LLM prompt for recommendations...");
                llmResponse = await _llmService.CompletePromptAsync(fallbackPrompt);
                Console.WriteLine($"\nRECEIVED FALLBACK LLM RESPONSE OF LENGTH: {llmResponse?.Length ?? 0} CHARACTERS");
                
                if (string.IsNullOrEmpty(llmResponse))
                {
                    Console.WriteLine("ERROR: Failed to get any recommendations from LLM");
                    _logger.LogError("Failed to get recommendations from LLM with fallback prompt");
                    return Array.Empty<IssueRecommendation>();
                }
            }

            Console.WriteLine($"\nLLM RESPONSE FOR RECOMMENDATIONS:\n{llmResponse}");
            
            // Process the response into structured recommendations
            var recommendations = ParseRecommendationsFromLLMResponse(llmResponse, recentIssues);
            
            Console.WriteLine($"\nFINAL RECOMMENDATIONS ({recommendations.Length}):");
            foreach (var rec in recommendations)
            {
                Console.WriteLine($"\n* {rec.Title} (Priority: {rec.Priority})");
                Console.WriteLine($"  Supporting Issues: {string.Join(", ", rec.SupportingIssues.Select(i => $"#{i}"))}");
                Console.WriteLine($"  {rec.Description.Substring(0, Math.Min(rec.Description.Length, 100))}...");
            }
            
            return recommendations;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: Failed to generate recommendations: {ex.Message}");
            _logger.LogError(ex, "Failed to generate recommendations using LLM: {Message}", ex.Message);
            return Array.Empty<IssueRecommendation>();
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
}

// Fix the interface to not duplicate IAsyncObserver methods
public interface IGitHubAnalysisGAgent : IGAgent, IAsyncObserver<GitHubIssueEvent>
{
    Task HandleGitHubIssueEventAsync(GitHubIssueEvent @event);
} 