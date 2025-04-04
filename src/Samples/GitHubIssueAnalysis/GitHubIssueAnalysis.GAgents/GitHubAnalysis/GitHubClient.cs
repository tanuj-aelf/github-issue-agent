using GitHubIssueAnalysis.GAgents.Common;
using Serilog;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

public class GitHubClient
{
    private readonly HttpClient _httpClient;
    private readonly string _personalAccessToken;

    public GitHubClient(string personalAccessToken)
    {
        _personalAccessToken = personalAccessToken;
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("AevatarGitHubAnalyzer", "1.0"));
        
        if (!string.IsNullOrEmpty(personalAccessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", personalAccessToken);
        }
    }

    public async Task<List<GitHubIssueInfo>> GetRepositoryIssuesAsync(string owner, string repo, int maxIssues = 100, string issueState = "all")
    {
        try
        {
            Log.Logger.Information("Starting GitHub issue analysis for {Owner}/{Repo} with state filter {State}", owner, repo, issueState);
            
            var issues = new List<GitHubIssueInfo>();
            
            // First check if repository exists
            try 
            {
                var repoResponse = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}");
                if (!repoResponse.IsSuccessStatusCode)
                {
                    Log.Logger.Error("Repository {Owner}/{Repo} not found. Status: {Status}", owner, repo, repoResponse.StatusCode);
                    return issues;
                }
                
                Log.Logger.Information("Repository exists, proceeding with analysis");
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error checking repository: {Owner}/{Repo}", owner, repo);
                return issues;
            }
            
            // Normalize the state parameter
            issueState = issueState.ToLowerInvariant();
            
            // Validate state parameter
            if (issueState != "open" && issueState != "closed" && issueState != "all")
            {
                Log.Logger.Warning("Invalid state parameter: {State}, defaulting to 'all'", issueState);
                issueState = "all";
            }
            
            // Use direct API calls for everything - no more Octokit!
            await GetIssuesDirectApiAsync(owner, repo, issues, maxIssues, issueState);
            
            // If we still don't have enough issues, try one more method
            if (issues.Count < maxIssues / 2)
            {
                Log.Logger.Information("Still only have {Count} issues, trying one-by-one approach", issues.Count);
                await FetchIssuesOneByOneAsync(owner, repo, issues, maxIssues, issueState);
            }
            
            Log.Logger.Information("Analysis complete. Got {Count} issues from {Owner}/{Repo}", 
                issues.Count, owner, repo);
            
            // Sort open issues first
            return issues
                .OrderByDescending(i => i.Status?.ToLowerInvariant() == "open")
                .ThenByDescending(i => i.CreatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Fatal error in repository analysis");
            return new List<GitHubIssueInfo>();
        }
    }
    
    private async Task<int> GetIssuesDirectApiAsync(string owner, string repo, List<GitHubIssueInfo> issues, int maxIssues, string state = "all")
    {
        Log.Logger.Information("Using direct GitHub API calls to fetch issues with state: {State}", state);
        int fetchedCount = 0;
        int page = 1;
        int perPage = 30;
        
        try
        {
            // Get existing issue IDs to avoid duplicates
            var existingIds = issues.Select(i => i.Id).ToHashSet();
            
            bool hasMorePages = true;
            while (hasMorePages && fetchedCount < maxIssues)
            {
                // Construct the GitHub API URL
                string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/issues?state={state}&per_page={perPage}&page={page}&sort=created&direction=desc";
                
                var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    Log.Logger.Error("GitHub API returned error: {StatusCode} - {ReasonPhrase}", 
                        response.StatusCode, response.ReasonPhrase);
                    break;
                }
                
                var content = await response.Content.ReadAsStringAsync();
                var fetchedIssues = JsonSerializer.Deserialize<List<GitHubApiIssue>>(content, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                });
                
                if (fetchedIssues == null || fetchedIssues.Count == 0)
                {
                    hasMorePages = false;
                    break;
                }
                
                foreach (var issue in fetchedIssues)
                {
                    // Skip if it's a pull request or already in our list
                    if (issue.PullRequest != null || existingIds.Contains(issue.Number.ToString()))
                        continue;
                    
                    issues.Add(new GitHubIssueInfo
                    {
                        Id = issue.Number.ToString(),
                        Title = issue.Title ?? "Untitled Issue",
                        Description = issue.Body ?? string.Empty,
                        Status = issue.State ?? "unknown",
                        CreatedAt = issue.CreatedAt ?? DateTime.UtcNow,
                        Labels = issue.Labels?.Select(l => l.Name).Where(n => n != null).Select(n => n!).ToArray() ?? Array.Empty<string>(),
                        Url = issue.HtmlUrl ?? $"https://github.com/{owner}/{repo}/issues/{issue.Number}",
                        Repository = $"{owner}/{repo}"
                    });
                    
                    existingIds.Add(issue.Number.ToString());
                    fetchedCount++;
                    
                    if (fetchedCount >= maxIssues)
                        break;
                }
                
                // Check for next page in the Link header
                if (response.Headers.TryGetValues("Link", out var linkValues))
                {
                    string linkHeader = linkValues.FirstOrDefault() ?? "";
                    hasMorePages = linkHeader.Contains("rel=\"next\"");
                }
                else
                {
                    hasMorePages = false;
                }
                
                // Move to the next page
                page++;
            }
            
            Log.Logger.Information("Direct API call completed, found {Count} issues with state: {State}", fetchedCount, state);
            return fetchedCount;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error in direct GitHub API call");
            return fetchedCount;
        }
    }

    private async Task FetchIssuesOneByOneAsync(string owner, string repo, List<GitHubIssueInfo> issues, int maxIssues, string state = "all")
    {
        try
        {
            Log.Logger.Information("Resorting to fetching issues one-by-one (last resort)");
            
            // Get list of existing issue IDs to avoid duplicates
            var existingIds = issues.Select(i => i.Id).ToHashSet();
            
            // Step 1: Try to get the latest issue number to estimate the repository size
            int latestIssueNumber = await GetLatestIssueNumberAsync(owner, repo);
            Log.Logger.Information("Latest issue/PR number is approximately: {Number}", latestIssueNumber);
            
            // Step 2: Create a sampling strategy based on repo size
            var issueNumbersToTry = new List<int>();
            
            if (latestIssueNumber > 0)
            {
                // For newer repos, try every issue
                if (latestIssueNumber <= 50)
                {
                    issueNumbersToTry.AddRange(Enumerable.Range(1, latestIssueNumber));
                }
                // For medium repos, sample more heavily from recent issues
                else if (latestIssueNumber <= 200)
                {
                    // Try the 20 most recent
                    issueNumbersToTry.AddRange(Enumerable.Range(latestIssueNumber - 19, 20));
                    
                    // And sample older issues
                    for (int i = latestIssueNumber - 20; i > 0; i -= 5)
                    {
                        issueNumbersToTry.Add(i);
                    }
                }
                // For large repos, be more selective
                else
                {
                    // Try the 20 most recent
                    issueNumbersToTry.AddRange(Enumerable.Range(latestIssueNumber - 19, 20));
                    
                    // Sample from the middle range
                    int midpoint = latestIssueNumber / 2;
                    issueNumbersToTry.AddRange(Enumerable.Range(midpoint - 10, 20));
                    
                    // And add some older issues with larger gaps
                    for (int i = midpoint - 30; i > 0; i -= 20)
                    {
                        issueNumbersToTry.Add(i);
                    }
                }
            }
            else
            {
                // Fallback if we couldn't determine the latest issue
                issueNumbersToTry.AddRange(Enumerable.Range(1, 50));
            }
            
            // Prioritize open issues first
            foreach (var issueNumber in issueNumbersToTry)
            {
                if (issues.Count >= maxIssues || existingIds.Contains(issueNumber.ToString()))
                    continue;
                
                try
                {
                    var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}");
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        // Issue number doesn't exist
                        continue;
                    }
                    
                    var content = await response.Content.ReadAsStringAsync();
                    var issue = JsonSerializer.Deserialize<GitHubApiIssue>(content, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        
                    if (issue == null)
                        continue;
                        
                    // Skip PRs
                    if (issue.PullRequest != null)
                    {
                        Log.Logger.Debug("Skipping PR #{Number}", issue.Number);
                        continue;
                    }
                    
                    // If issue doesn't match the requested state, skip it
                    if (state != "all" && issue.State?.ToLowerInvariant() != state.ToLowerInvariant())
                    {
                        Log.Logger.Debug("Skipping issue #{Number} with state {State} as it doesn't match requested state {RequestedState}", 
                            issue.Number, issue.State, state);
                        continue;
                    }
                    
                    var issueInfo = new GitHubIssueInfo
                    {
                        Id = issue.Number.ToString(),
                        Title = issue.Title ?? "Untitled Issue",
                        Description = issue.Body ?? string.Empty,
                        Labels = issue.Labels?.Select(l => l.Name).Where(n => n != null).Select(n => n!).ToArray() ?? Array.Empty<string>(),
                        Url = issue.HtmlUrl ?? $"https://github.com/{owner}/{repo}/issues/{issue.Number}",
                        Repository = $"{owner}/{repo}",
                        CreatedAt = issue.CreatedAt ?? DateTime.UtcNow,
                        Status = issue.State ?? "unknown"
                    };
                    
                    issues.Add(issueInfo);
                    existingIds.Add(issue.Number.ToString());
                    
                    // Log details
                    if (issue.Labels?.Count > 0)
                    {
                        Log.Logger.Information("Issue #{Number} ({Status}) has {Count} labels: {Labels}", 
                            issue.Number, issue.State, issue.Labels.Count, 
                            string.Join(", ", issue.Labels.Select(l => l.Name)));
                    }
                    else
                    {
                        Log.Logger.Information("Issue #{Number} ({Status}) has no labels", 
                            issue.Number, issue.State);
                    }
                    
                    Log.Logger.Information("Found individual issue #{Number}: {Title} ({Status})", 
                        issue.Number, issue.Title, issue.State);
                    
                    if (issues.Count >= maxIssues)
                        break;
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Error fetching issue #{Number}", issueNumber);
                }
            }
            
            Log.Logger.Information("One-by-one fetch completed, found {Count} total issues", issues.Count);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error in one-by-one fetching method");
        }
    }
    
    private async Task<int> GetLatestIssueNumberAsync(string owner, string repo)
    {
        try
        {
            // Try to get the latest issue to determine the rough number range
            var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/issues?per_page=1&state=all");
            
            if (!response.IsSuccessStatusCode)
                return 50; // Default if we can't get issues
                
            var content = await response.Content.ReadAsStringAsync();
            var issues = JsonSerializer.Deserialize<List<GitHubApiIssue>>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
            // Explicitly cast the long to int, or use a reasonable default if too large
            var latestIssueNumber = issues?.FirstOrDefault()?.Number ?? 0;
            if (latestIssueNumber > int.MaxValue)
            {
                Log.Logger.Warning("Issue number {Number} exceeds int.MaxValue, using 1000 as a default", latestIssueNumber);
                return 1000; // Use a reasonable default
            }
            
            return (int)latestIssueNumber; // Explicit cast
        }
        catch
        {
            // If we can't determine the latest issue, default to 50
            return 50;
        }
    }

    public async Task<GitHubIssueInfo> GetIssueAsync(string owner, string repo, int issueNumber)
    {
        try
        {
            var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}");
            
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Issue #{issueNumber} not found or access denied. Status: {response.StatusCode}");
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var issue = JsonSerializer.Deserialize<GitHubApiIssue>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
            if (issue == null)
                throw new Exception($"Failed to parse issue data for #{issueNumber}");
                
            // Verify this is not a PR
            if (issue.PullRequest != null)
            {
                throw new InvalidOperationException($"Issue #{issueNumber} is a pull request, not an issue");
            }
            
            return new GitHubIssueInfo
            {
                Id = issue.Number.ToString(),
                Title = issue.Title ?? "Untitled Issue",
                Description = issue.Body ?? string.Empty,
                Labels = issue.Labels?.Select(l => l.Name).Where(n => n != null).Select(n => n!).ToArray() ?? Array.Empty<string>(),
                Url = issue.HtmlUrl ?? $"https://github.com/{owner}/{repo}/issues/{issue.Number}",
                Repository = $"{owner}/{repo}",
                CreatedAt = issue.CreatedAt ?? DateTime.UtcNow,
                Status = issue.State ?? "unknown"
            };
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error fetching issue #{IssueNumber} for repository {Owner}/{Repo}", issueNumber, owner, repo);
            throw;
        }
    }

    public async Task<List<GitHubIssueInfo>> FetchIssuesAsync(string owner, string repo, int maxIssues, string state = "all")
    {
        return await GetRepositoryIssuesAsync(owner, repo, maxIssues, state);
    }

    // Model classes for direct GitHub API parsing
    private class GitHubApiIssue
    {
        public long Number { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? State { get; set; }
        public DateTime? CreatedAt { get; set; }
        public List<GitHubApiLabel>? Labels { get; set; }
        public string? HtmlUrl { get; set; }
        public GitHubApiPullRequest? PullRequest { get; set; }
    }
    
    private class GitHubApiLabel
    {
        public string? Name { get; set; }
    }
    
    private class GitHubApiPullRequest
    {
        public string? Url { get; set; }
    }
} 