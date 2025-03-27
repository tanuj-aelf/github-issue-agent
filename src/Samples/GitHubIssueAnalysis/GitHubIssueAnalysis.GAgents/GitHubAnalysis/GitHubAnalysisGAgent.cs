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

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

// Static helper for stream names and IDs
public static class GitHubAnalysisStream
{
    public static readonly Guid TagsStreamKey = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid IssuesStreamKey = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly string StreamNamespace = "GitHubAnalysisStream";
}

// Fix the ImplicitStreamSubscription to use the static StreamNamespace
[ImplicitStreamSubscription("GitHubAnalysisStream")]
[Reentrant]
public class GitHubAnalysisGAgent : GAgentBase<GitHubAnalysisState, GitHubAnalysisLogEvent>, IGitHubAnalysisGAgent, IGrainWithGuidKey
{
    private readonly ILogger<GitHubAnalysisGAgent> _logger;
    private readonly ILLMService _llmService;
    
    // Use the correct type for stream subscription handle
    private StreamSubscriptionHandle<GitHubIssueEvent>? _streamSubscription;
    
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
        
        // Also register a timer to retry subscription periodically until it succeeds
        this.RegisterTimer(
            async _ => 
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
                        StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.IssuesStreamKey));
                    
                    // If we have a subscription but lost connection, reconnect
                    _logger.LogInformation("Checking subscription status from timer...");
                }
                catch
                {
                    // If any errors, try to resubscribe
                    _logger.LogWarning("Error checking subscription status, attempting to resubscribe...");
                    await SetupStreamSubscriptionAsync();
                }
            },
            null,
            TimeSpan.FromSeconds(1),  // Start after 1 second
            TimeSpan.FromSeconds(5)); // Check every 5 seconds
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
                var issuesStreamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.IssuesStreamKey);
                _logger.LogWarning("====== SUBSCRIBING TO ISSUES STREAM: {Namespace}/{Key} ======", 
                    GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.IssuesStreamKey);
                
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
                    var summaryStreamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, Guid.Empty);
                    _logger.LogWarning("====== SUBSCRIBING TO SUMMARY STREAM: {Namespace}/{Key} ======", 
                        GitHubAnalysisStream.StreamNamespace, Guid.Empty);
                    
                    var summaryStream = streamProvider.GetStream<SummaryReportEvent>(summaryStreamId);
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
            _logger.LogWarning("Received tags event for issue: {IssueId}", item.IssueId);
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
    private class SummaryStreamObserver : IAsyncObserver<SummaryReportEvent>
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        
        public SummaryStreamObserver(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger;
        }
        
        public Task OnNextAsync(SummaryReportEvent item, StreamSequenceToken? token = null)
        {
            _logger.LogWarning("====================================================");
            _logger.LogWarning("    RECEIVED SUMMARY EVENT FROM STREAM              ");
            _logger.LogWarning("====================================================");
            _logger.LogWarning("Repository: {Repository}", item.Repository);
            _logger.LogWarning("Total Issues: {TotalIssues}", item.TotalIssuesAnalyzed);
            _logger.LogWarning("Stream Token: {Token}", token);
            _logger.LogWarning("====================================================");
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
        try
        {
            return this.GetStreamProvider("Aevatar");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get 'Aevatar' stream provider, falling back to 'MemoryStreams'");
            return this.GetStreamProvider("MemoryStreams");
        }
    }

    // Explicit handler for ImplicitStreamSubscription 
    public Task OnNextAsync(GitHubIssueEvent @event, StreamSequenceToken? token = null)
    {
        try
        {
            _logger.LogWarning("====================================================");
            _logger.LogWarning("    RECEIVED EVENT FROM STREAM                     ");
            _logger.LogWarning("====================================================");
            _logger.LogWarning("Issue Title: {@IssueTitle}", @event.IssueInfo.Title);
            _logger.LogWarning("Repository: {@Repository}", @event.IssueInfo.Repository);
            _logger.LogWarning("Stream Token: {Token}", token);
            _logger.LogWarning("Grain ID: {GrainId}", this.GetPrimaryKey());
            _logger.LogWarning("====================================================");
            
            // Process the event in the current Orleans thread - Orleans will handle concurrency
            // This ensures we don't miss messages due to background task timing issues
            HandleGitHubIssueEventAsync(@event).Ignore();
            
            // Return completed task since processing is happening in the background
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnNextAsync handler");
            return Task.CompletedTask;
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
            _logger.LogInformation($"{nameof(GitHubAnalysisGAgent)} received {nameof(GitHubIssueEvent)} for repository: {@event.IssueInfo.Repository}");

            var issueInfo = @event.IssueInfo;
            
            // Store issue in state
            if (!State.RepositoryIssues.ContainsKey(issueInfo.Repository))
            {
                State.RepositoryIssues[issueInfo.Repository] = new List<GitHubIssueInfo>();
            }
            
            State.RepositoryIssues[issueInfo.Repository].Add(issueInfo);

            // Extract tags from the issue using LLM
            string[] extractedTags;
            try
            {
                _logger.LogInformation("Attempting to extract tags using LLM for issue: {IssueTitle}", issueInfo.Title);
                extractedTags = await ExtractTagsUsingLLMAsync(issueInfo);
                _logger.LogInformation("Successfully extracted tags: {Tags}", string.Join(", ", extractedTags));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract tags using LLM, falling back to basic extraction");
                extractedTags = ExtractBasicTagsFromIssue(issueInfo);
            }
            
            // Store tags in state
            if (!State.IssueTags.ContainsKey(issueInfo.Repository))
            {
                State.IssueTags[issueInfo.Repository] = new Dictionary<string, List<string>>();
            }
            
            State.IssueTags[issueInfo.Repository][issueInfo.Id] = extractedTags.ToList();

            // Publish the extracted tags event if we have a stream provider
            try
            {
                var tagsEvent = new IssueTagsEvent
                {
                    IssueId = issueInfo.Id,
                    Title = issueInfo.Title,
                    ExtractedTags = extractedTags,
                    Repository = issueInfo.Repository
                };
                
                _logger.LogInformation("Attempting to get stream provider to publish tags");
                var streamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, GitHubAnalysisStream.TagsStreamKey);
                var stream = GetAppropriateStreamProvider().GetStream<IssueTagsEvent>(streamId);
                await stream.OnNextAsync(tagsEvent);
                
                _logger.LogInformation("Successfully published tags event for issue {IssueId}", issueInfo.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CRITICAL ERROR: Failed to publish tags event. Details: {ExceptionMessage}", ex.Message);
            }

            // Check if we should generate a summary report (when we have analyzed enough issues)
            if (State.RepositoryIssues[issueInfo.Repository].Count % 5 == 0 || 
                State.RepositoryIssues[issueInfo.Repository].Count == 1)
            {
                try
                {
                    _logger.LogInformation("Generating summary report for repository: {Repository}", issueInfo.Repository);
                    await GenerateSummaryReportAsync(issueInfo.Repository);
                    _logger.LogInformation("Successfully generated summary report for repository: {Repository}", issueInfo.Repository);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate summary report for repository: {Repository}", issueInfo.Repository);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FATAL ERROR in HandleGitHubIssueEventInternalAsync: {ExceptionMessage}", ex.Message);
        }
    }

    private async Task<string[]> ExtractTagsUsingLLMAsync(GitHubIssueInfo issueInfo)
    {
        try
        {
            _logger.LogWarning("========== STARTING LLM CALL FOR TAG EXTRACTION ==========");
            _logger.LogWarning("Issue: {IssueTitle}", issueInfo.Title);
            
            string prompt = $@"
Analyze the following GitHub issue and extract relevant tags/categories that describe the issue.
Return only a comma-separated list of tags (5-10 tags).

Title: {issueInfo.Title}
Description: {issueInfo.Description}
Existing Labels: {string.Join(", ", issueInfo.Labels)}
Status: {issueInfo.Status}
";

            _logger.LogWarning("LLM Service Type: {LLMServiceType}", _llmService.GetType().Name);
            _logger.LogWarning("Calling LLM service with prompt length: {Length}", prompt.Length);
            _logger.LogWarning("First 100 chars of prompt: {PromptStart}", prompt.Substring(0, Math.Min(100, prompt.Length)));
            
            var tags = await _llmService.CompletePromptAsync(prompt);
            
            _logger.LogWarning("========== COMPLETED LLM CALL FOR TAG EXTRACTION ==========");
            _logger.LogWarning("LLM Response: {Response}", tags);
            
            if (string.IsNullOrWhiteSpace(tags))
            {
                _logger.LogWarning("LLM returned empty tags for issue {IssueId}, falling back to basic extraction", issueInfo.Id);
                return ExtractBasicTagsFromIssue(issueInfo);
            }
            
            var tagArray = tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Distinct()
                .ToArray();
                
            _logger.LogWarning("Successfully extracted {Count} tags: {Tags}", 
                tagArray.Length, string.Join(", ", tagArray));
                
            return tagArray;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting tags using LLM for issue {IssueId}, falling back to basic extraction", issueInfo.Id);
            return ExtractBasicTagsFromIssue(issueInfo);
        }
    }

    private string[] ExtractBasicTagsFromIssue(GitHubIssueInfo issueInfo)
    {
        try
        {
            var basicTags = new List<string>();
            
            // Add any existing labels as tags
            if (issueInfo.Labels != null && issueInfo.Labels.Length > 0)
            {
                basicTags.AddRange(issueInfo.Labels);
            }
            
            // Extract simple keywords from title
            string[] keywords = { "bug", "feature", "enhancement", "documentation", "question", "security", "performance" };
            foreach (var keyword in keywords)
            {
                if (issueInfo.Title.ToLower().Contains(keyword.ToLower()) || 
                    (issueInfo.Description != null && issueInfo.Description.ToLower().Contains(keyword.ToLower())))
                {
                    basicTags.Add(keyword);
                }
            }
            
            // Add status as a tag
            if (!string.IsNullOrEmpty(issueInfo.Status))
            {
                basicTags.Add(issueInfo.Status);
            }
            
            return basicTags.Distinct().ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting tags from issue {IssueId}", issueInfo.Id);
            return Array.Empty<string>();
        }
    }

    private async Task GenerateSummaryReportAsync(string repository)
    {
        try
        {
            _logger.LogInformation($"Generating summary report for repository: {repository}");
            
            // Get all issues for the repository
            var issues = State.RepositoryIssues[repository];
            
            // Get all tags for the repository
            var allTags = new List<string>();
            foreach (var issueTags in State.IssueTags[repository].Values)
            {
                allTags.AddRange(issueTags);
            }
            
            // Calculate tag frequencies
            var tagFrequency = allTags
                .GroupBy(tag => tag)
                .ToDictionary(g => g.Key, g => g.Count());
            
            // Generate priority recommendations using LLM
            var priorityRecommendations = await GenerateRecommendationsUsingLLMAsync(repository, issues, tagFrequency);
            
            // Create the summary report event
            var summaryEvent = new SummaryReportEvent
            {
                Repository = repository,
                TagFrequency = tagFrequency,
                PriorityRecommendations = priorityRecommendations,
                GeneratedAt = DateTime.UtcNow,
                TotalIssuesAnalyzed = State.RepositoryIssues[repository].Count
            };
            
            // Publish to both Orleans streams for redundancy
            try
            {
                // Create a unique stream ID with empty Guid which clients will listen on
                var streamId = StreamId.Create(GitHubAnalysisStream.StreamNamespace, Guid.Empty);
                _logger.LogWarning("====== PUBLISHING SUMMARY REPORT TO STREAM: {Namespace}/{Key} ======", 
                    GitHubAnalysisStream.StreamNamespace, Guid.Empty);
                
                // First try with Aevatar provider
                try
                {
                    var streamProvider = this.GetStreamProvider("Aevatar");
                    var stream = streamProvider.GetStream<SummaryReportEvent>(streamId);
                    await stream.OnNextAsync(summaryEvent);
                    _logger.LogWarning("Successfully published summary report to Aevatar stream provider");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish to Aevatar stream provider");
                }
                
                // Then try with MemoryStreams provider
                try
                {
                    var streamProvider = this.GetStreamProvider("MemoryStreams");
                    var stream = streamProvider.GetStream<SummaryReportEvent>(streamId);
                    await stream.OnNextAsync(summaryEvent);
                    _logger.LogWarning("Successfully published summary report to MemoryStreams provider");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish to MemoryStreams provider");
                }
                
                // Now try our wrapper method
                try
                {
                    var streamProvider = GetAppropriateStreamProvider();
                    var stream = streamProvider.GetStream<SummaryReportEvent>(streamId);
                    await stream.OnNextAsync(summaryEvent);
                    _logger.LogWarning("Successfully published summary report via GetAppropriateStreamProvider");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish via GetAppropriateStreamProvider");
                }
                
                _logger.LogInformation("Published summary report for repository {Repository} with streamId {StreamId}", repository, streamId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing summary report for repository {Repository}", repository);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating summary report for repository {Repository}", repository);
        }
    }

    private async Task<List<string>> GenerateRecommendationsUsingLLMAsync(string repository, List<GitHubIssueInfo> issues, Dictionary<string, int> tagFrequency)
    {
        try
        {
            _logger.LogWarning("========== STARTING LLM CALL FOR RECOMMENDATIONS GENERATION ==========");
            _logger.LogWarning("Repository: {Repository}, Issue Count: {IssueCount}", repository, issues.Count);
            
            // Create a summary of the issues and tags
            var topIssues = issues.Take(10).ToList();
            var topTags = tagFrequency.OrderByDescending(kv => kv.Value).Take(10).ToList();
            
            string tagsStr = string.Join("\n", topTags.Select(t => $"- {t.Key}: {t.Value} issues"));
            string issuesStr = string.Join("\n", topIssues.Select(i => $"- {i.Title} [{string.Join(", ", State.IssueTags[repository][i.Id])}]"));
            
            string prompt = $@"
Analyze the following GitHub repository data and provide strategic recommendations for the development team.
Return 3-5 prioritized recommendations, each on a new line.

Repository: {repository}
Number of Issues: {issues.Count}

Top Tags:
{tagsStr}

Sample Issues:
{issuesStr}

Based on this data, what are the 3-5 most important priorities for the development team?
";

            _logger.LogInformation("Calling LLM service with recommendations prompt of length {Length}", prompt.Length);
            var recommendations = await _llmService.CompletePromptAsync(prompt);
            _logger.LogWarning("========== COMPLETED LLM CALL FOR RECOMMENDATIONS GENERATION ==========");
            
            if (string.IsNullOrWhiteSpace(recommendations))
            {
                _logger.LogWarning("LLM returned empty recommendations for repository {Repository}, falling back to basic recommendations", repository);
                return GenerateBasicRecommendations(repository, tagFrequency);
            }
            
            var recommendationList = recommendations
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.StartsWith("-") ? r.Substring(1).Trim() : r)
                .ToList();
                
            _logger.LogInformation("Successfully generated {Count} recommendations for repository {Repository}", 
                recommendationList.Count, repository);
                
            return recommendationList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating recommendations using LLM for repository {Repository}, falling back to basic recommendations", repository);
            return GenerateBasicRecommendations(repository, tagFrequency);
        }
    }
    
    private List<string> GenerateBasicRecommendations(string repository, Dictionary<string, int> tagFrequency)
    {
        var recommendations = new List<string>();
        
        // If we have no issues, return generic recommendations
        if (!State.RepositoryIssues.ContainsKey(repository) || State.RepositoryIssues[repository].Count == 0)
        {
            recommendations.Add("Initialize repository with a proper README and documentation");
            recommendations.Add("Set up CI/CD pipelines for automated testing and deployment");
            recommendations.Add("Establish coding standards and contribution guidelines");
            return recommendations;
        }
        
        // Get top tags
        var topTags = tagFrequency.OrderByDescending(kv => kv.Value).Take(3).ToList();
        
        // Add recommendations based on top tags
        foreach (var tag in topTags)
        {
            switch (tag.Key.ToLower())
            {
                case "bug":
                    recommendations.Add($"Fix reported bugs (found in {tag.Value} issues)");
                    break;
                case "feature":
                    recommendations.Add($"Implement requested features (found in {tag.Value} issues)");
                    break;
                case "enhancement":
                    recommendations.Add($"Enhance existing functionality (found in {tag.Value} issues)");
                    break;
                case "documentation":
                    recommendations.Add($"Improve documentation (found in {tag.Value} issues)");
                    break;
                case "security":
                    recommendations.Add($"Address security concerns (found in {tag.Value} issues)");
                    break;
                case "performance":
                    recommendations.Add($"Optimize performance (found in {tag.Value} issues)");
                    break;
                default:
                    recommendations.Add($"Focus on {tag.Key} (found in {tag.Value} issues)");
                    break;
            }
        }
        
        // Add a general recommendation
        recommendations.Add("Regularly review and triage open issues");
        
        return recommendations;
    }

    // Add the additional required IAsyncObserver methods
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