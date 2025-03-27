using Aevatar.Core;
using Aevatar.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text.Json;
using GitHubIssueAnalysis.GAgents.Common;
using GitHubIssueAnalysis.GAgents.Services;
using Orleans.Streams;
using Orleans.Runtime;
using Orleans;
using Orleans.Concurrency;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

[ImplicitStreamSubscription("GitHubAnalysisStream")]
[Reentrant]
public class GitHubAnalysisGAgent : GAgentBase<GitHubAnalysisState, GitHubAnalysisLogEvent>, IGitHubAnalysisGAgent, IGrainWithGuidKey
{
    private readonly ILogger<GitHubAnalysisGAgent> _logger;
    private readonly ILLMService _llmService;
    
    public GitHubAnalysisGAgent(
        ILogger<GitHubAnalysisGAgent> logger, 
        ILLMService llmService)
    {
        _logger = logger;
        _llmService = llmService;
    }

    public override Task<string> GetDescriptionAsync()
    {
        return Task.FromResult("GitHub Issue Analysis Agent: Analyzes GitHub repository issues, extracts common themes, and helps prioritize development work.");
    }

    [EventHandler]
    public async Task HandleGitHubIssueEventAsync(GitHubIssueEvent @event)
    {
        await HandleGitHubIssueEventInternalAsync(@event);
    }

    // This is now called from the stream subscription
    private async Task HandleGitHubIssueEventInternalAsync(GitHubIssueEvent @event)
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
        var extractedTags = await ExtractTagsUsingLLMAsync(issueInfo);
        
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
            
            var streamProvider = this.GetStreamProvider("MemoryStreams");
            var streamId = StreamId.Create("GitHubAnalysisStream", "tags");
            var stream = streamProvider.GetStream<IssueTagsEvent>(streamId);
            await stream.OnNextAsync(tagsEvent);
            
            _logger.LogInformation("Published tags event for issue {IssueId}", issueInfo.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing tags event");
        }

        // Check if we should generate a summary report (when we have analyzed enough issues)
        if (State.RepositoryIssues[issueInfo.Repository].Count % 5 == 0 || 
            State.RepositoryIssues[issueInfo.Repository].Count == 1)
        {
            await GenerateSummaryReportAsync(issueInfo.Repository);
        }
    }

    private async Task<string[]> ExtractTagsUsingLLMAsync(GitHubIssueInfo issueInfo)
    {
        try
        {
            string prompt = $@"
Analyze the following GitHub issue and extract relevant tags/categories that describe the issue.
Return only a comma-separated list of tags (5-10 tags).

Title: {issueInfo.Title}
Description: {issueInfo.Description}
Existing Labels: {string.Join(", ", issueInfo.Labels)}
Status: {issueInfo.Status}
";

            var tags = await _llmService.CompletePromptAsync(prompt);
            
            if (string.IsNullOrWhiteSpace(tags))
            {
                _logger.LogWarning("LLM returned empty tags for issue {IssueId}, falling back to basic extraction", issueInfo.Id);
                return ExtractBasicTagsFromIssue(issueInfo);
            }
            
            return tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Distinct()
                .ToArray();
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
            
            // Publish to Orleans stream
            try
            {
                // Create a unique stream ID for this report to enable client subscription
                var streamId = StreamId.Create("GitHubAnalysisStream", Guid.NewGuid().ToString());
                
                // Get the stream provider and stream
                var streamProvider = this.GetStreamProvider("MemoryStreams");
                var stream = streamProvider.GetStream<SummaryReportEvent>(streamId);
                
                // Publish to the stream
                await stream.OnNextAsync(summaryEvent);
                
                _logger.LogInformation("Published summary report for repository {Repository}", repository);
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

            var recommendations = await _llmService.CompletePromptAsync(prompt);
            
            if (string.IsNullOrWhiteSpace(recommendations))
            {
                _logger.LogWarning("LLM returned empty recommendations for repository {Repository}, falling back to basic recommendations", repository);
                return GenerateBasicRecommendations(repository, tagFrequency);
            }
            
            return recommendations
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.StartsWith("-") ? r.Substring(1).Trim() : r)
                .ToList();
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
}

public interface IGitHubAnalysisGAgent : IGAgent
{
    Task HandleGitHubIssueEventAsync(GitHubIssueEvent @event);
} 