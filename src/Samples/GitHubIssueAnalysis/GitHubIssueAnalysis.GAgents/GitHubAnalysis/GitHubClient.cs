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
            
            // Check if maxIssues is too low when filtering
            if (maxIssues < 5 && issueState != "all")
            {
                Log.Logger.Warning("Maximum issue count ({MaxIssues}) is very low. Consider increasing to get a better sample.", maxIssues);
            }
            
            // Special handling for when we're looking for exactly 1 closed issue
            if (maxIssues == 1 && issueState == "closed")
            {
                Log.Logger.Information("Attempting to find a single user-reported closed issue...");
                
                // Try direct search API call first with special filtering
                // Direct search for closed issues that are not pull requests
                await GetIssuesDirectApiAsync(owner, repo, issues, maxIssues, issueState);
                
                // If we didn't get anything, try with additional search qualifiers
                if (issues.Count == 0)
                {
                    Log.Logger.Information("No suitable closed issues found via API call, trying extended search");
                    
                    try 
                    {
                        // Try a direct search API call with additional qualifiers
                        // Search for issues that aren't authored by the repo owner and contain common issue terms
                        string searchQuery = $"repo:{owner}/{repo} is:issue state:closed -author:{owner}";
                        string apiUrl = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(searchQuery)}&per_page=1";
                        
                        Log.Logger.Information("Trying extended search: {Url}", apiUrl);
                        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                        var response = await _httpClient.SendAsync(request);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync();
                            var searchResult = JsonSerializer.Deserialize<GitHubSearchResult>(content, 
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                
                            if (searchResult?.Items != null && searchResult.Items.Count > 0)
                            {
                                var issue = searchResult.Items[0];
                                Log.Logger.Information("Found issue #{Number} with extended search: {Title}", issue.Number, issue.Title);
                                
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
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error(ex, "Error during extended search");
                    }
                }
                
                // If we still don't have anything, try one-by-one approach as last resort
                if (issues.Count == 0)
                {
                    Log.Logger.Information("Still no issues found, trying one-by-one approach");
                    await FetchIssuesOneByOneAsync(owner, repo, issues, maxIssues, issueState);
                }
            }
            else
            {
                // Regular approach for all other cases
                // Use direct API calls for everything - no more Octokit!
                await GetIssuesDirectApiAsync(owner, repo, issues, maxIssues, issueState);
                
                // Special handling for "all" issues when nothing found
                if (issues.Count == 0 && issueState == "all")
                {
                    Log.Logger.Information("No issues found with 'all' state filter. Trying specific searches...");
                    
                    // First try closed issues
                    var closedIssues = new List<GitHubIssueInfo>();
                    await GetIssuesDirectApiAsync(owner, repo, closedIssues, maxIssues, "closed");
                    
                    if (closedIssues.Count > 0)
                    {
                        Log.Logger.Information("Found {Count} closed issues", closedIssues.Count);
                        issues.AddRange(closedIssues);
                    }
                    
                    // Then try open issues
                    if (issues.Count < maxIssues)
                    {
                        var openIssues = new List<GitHubIssueInfo>();
                        await GetIssuesDirectApiAsync(owner, repo, openIssues, maxIssues - issues.Count, "open");
                        
                        if (openIssues.Count > 0)
                        {
                            Log.Logger.Information("Found {Count} open issues", openIssues.Count);
                            issues.AddRange(openIssues);
                        }
                    }
                    
                    // If we still don't have any, try a more lenient search query
                    if (issues.Count == 0)
                    {
                        try
                        {
                            // Try with a more basic query that just looks for issues with content
                            string searchQuery = $"repo:{owner}/{repo} is:issue";
                            string apiUrl = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(searchQuery)}&per_page={maxIssues}";
                            
                            Log.Logger.Information("Trying basic search: {Url}", apiUrl);
                            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                            var response = await _httpClient.SendAsync(request);
                            
                            if (response.IsSuccessStatusCode)
                            {
                                var content = await response.Content.ReadAsStringAsync();
                                var searchResult = JsonSerializer.Deserialize<GitHubSearchResult>(content, 
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                    
                                if (searchResult?.Items != null && searchResult.Items.Count > 0)
                                {
                                    Log.Logger.Information("Basic search found {Count} issues", searchResult.Items.Count);
                                    foreach (var issue in searchResult.Items.Take(maxIssues))
                                    {
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
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Error(ex, "Error during basic issue search");
                        }
                    }
                }
                
                // If we still don't have enough issues, try one more method
                if (issues.Count < maxIssues / 2)
                {
                    Log.Logger.Information("Still only have {Count} issues, trying one-by-one approach", issues.Count);
                    await FetchIssuesOneByOneAsync(owner, repo, issues, maxIssues, issueState);
                }
            }
            
            // Filter issues by state if needed
            if (issueState != "all")
            {
                var filteredIssues = issues.Where(i => i.Status?.ToLowerInvariant() == issueState.ToLowerInvariant()).ToList();
                Log.Logger.Information("Filtered {TotalCount} issues down to {FilteredCount} with state '{State}'", 
                    issues.Count, filteredIssues.Count, issueState);
                
                // If we didn't find any issues with the requested state, try another API call specifically for that state
                if (filteredIssues.Count == 0 && issues.Count > 0)
                {
                    Log.Logger.Warning("No issues found with state '{State}' after filtering. Attempting direct API call with state filter.", issueState);
                    
                    // Try one more direct API call with the specific state
                    await GetIssuesDirectApiAsync(owner, repo, filteredIssues, maxIssues, issueState);
                    
                    Log.Logger.Information("After specific API call for state '{State}', found {Count} issues", 
                        issueState, filteredIssues.Count);
                }
                
                issues = filteredIssues;
            }
            
            // Filter out common PR-related title patterns if we have more issues than maxIssues
            if (issues.Count > maxIssues)
            {
                var prKeywords = new[] { "dev", "development", "merge", "update", "fix", "pr" };
                var interestingKeywords = new[] { "bug", "cannot", "error", "fail", "login", "issue", "problem" };
                
                // First prioritize issues with interesting keywords (like "cannot login")
                var interestingIssues = issues
                    .Where(i => interestingKeywords.Any(kw => 
                        i.Title.ToLowerInvariant().Contains(kw) || 
                        i.Description.ToLowerInvariant().Contains(kw)))
                    .ToList();
                
                // Then look for non-PR issues (not exactly matching PR keywords)
                var remainingNonPrIssues = issues
                    .Where(i => !interestingIssues.Contains(i) && 
                           !prKeywords.Any(kw => i.Title.ToLowerInvariant() == kw))
                    .ToList();
                
                // Combine both interesting and remaining non-PR issues
                var prioritizedIssues = interestingIssues
                    .Concat(remainingNonPrIssues)
                    .Take(maxIssues)
                    .ToList();
                
                // If we have enough prioritized issues, use them
                if (prioritizedIssues.Count >= maxIssues)
                {
                    Log.Logger.Information("Prioritized {InterestingCount} interesting issues and {NonPrCount} regular issues", 
                        interestingIssues.Count, Math.Min(remainingNonPrIssues.Count, maxIssues - interestingIssues.Count));
                    issues = prioritizedIssues;
                }
                // Otherwise, we'll need to include some PR issues to meet the maxIssues quota
                else if (prioritizedIssues.Count > 0)
                {
                    var prIssues = issues
                        .Where(i => !prioritizedIssues.Contains(i))
                        .Take(maxIssues - prioritizedIssues.Count)
                        .ToList();
                    
                    issues = prioritizedIssues.Concat(prIssues).ToList();
                    Log.Logger.Information("Using {PrioritizedCount} prioritized issues and {PrCount} PR-related issues", 
                        prioritizedIssues.Count, prIssues.Count);
                }
                // Limit to the maximum number of issues
                issues = issues.Take(maxIssues).ToList();
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
        Log.Logger.Information("Using GitHub search API to fetch true issues with state: {State}", state);
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
                // Construct the GitHub Search API URL with proper qualifiers
                // is:issue ensures we only get issues, not PRs
                string searchQuery;
                
                if (state == "all")
                {
                    // For "all" state, use a more aggressive filter to exclude PR-like content
                    // Exclude issues created by the repo owner and common PR-related terms
                    searchQuery = $"repo:{owner}/{repo} is:issue -author:{owner} -label:enhancement -label:feature -title:dev -title:merge -title:update -title:fix";
                }
                else
                {
                    // For specific states, use basic filtering
                    searchQuery = $"repo:{owner}/{repo} is:issue state:{state}";
                }
                
                string apiUrl = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(searchQuery)}&per_page={perPage}&page={page}&sort=updated&order=desc";
                
                Log.Logger.Information("Fetching issues using Search API: {Url}", apiUrl);
                var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    Log.Logger.Error("GitHub API returned error: {StatusCode} - {ReasonPhrase}", 
                        response.StatusCode, response.ReasonPhrase);
                    break;
                }
                
                var content = await response.Content.ReadAsStringAsync();
                var searchResult = JsonSerializer.Deserialize<GitHubSearchResult>(content, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (searchResult?.Items == null || searchResult.Items.Count == 0)
                {
                    hasMorePages = false;
                    break;
                }
                
                // Log the total count of issues found by the search
                Log.Logger.Information("Search API found {TotalCount} true issues matching the query", searchResult.TotalCount);
                
                // Log issue numbers we got from the API for debugging
                Log.Logger.Information("Search API returned issue numbers: {Numbers}", 
                    string.Join(", ", searchResult.Items.Select(i => i.Number)));
                
                foreach (var issue in searchResult.Items)
                {
                    // Skip if already in our list
                    if (existingIds.Contains(issue.Number.ToString()))
                    {
                        Log.Logger.Debug("Skipping duplicate issue #{Number}", issue.Number);
                        continue;
                    }
                    
                    Log.Logger.Debug("Processing issue #{Number}: '{Title}' with state: {State}", 
                        issue.Number, issue.Title, issue.State);
                    
                    // Log creator info for debugging
                    if (issue.User != null)
                    {
                        Log.Logger.Debug("Issue #{Number} was created by: {Creator}", issue.Number, issue.User.Login);
                    }
                    
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
                
                // Check for next page - increment page count if we didn't reach the max
                if (searchResult.Items.Count < perPage || fetchedCount >= maxIssues)
                {
                    hasMorePages = false;
                }
                else
                {
                    page++;
                }
            }
            
            Log.Logger.Information("Search API call completed, found {Count} true issues with state: {State}", fetchedCount, state);
            return fetchedCount;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error in GitHub Search API call");
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
            
            // For small repos or when requesting just 1 issue, try common issue numbers first
            // Many user-reported issues tend to be in the first few issues of a repo
            if (maxIssues == 1 || latestIssueNumber <= 20)
            {
                // Lower issue numbers are often community-reported bugs on public repos,
                // while higher numbers are usually PR-related
                for (int i = 1; i <= Math.Min(15, latestIssueNumber); i++)
                {
                    issueNumbersToTry.Add(i);
                }
            }
            
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
                    // Try the 20 most recent issues
                    issueNumbersToTry.AddRange(Enumerable.Range(latestIssueNumber - 19, 20));
                    
                    // Sample from the middle range
                    int midpoint = latestIssueNumber / 2;
                    issueNumbersToTry.AddRange(Enumerable.Range(midpoint - 10, 20));
                    
                    // And sample some older issues with larger gaps
                    for (int i = midpoint - 50; i > 0; i -= 10)
                    {
                        issueNumbersToTry.Add(i);
                    }
                }
                // For large repos, be more selective but ensure we get a good distribution
                else
                {
                    // Try the 20 most recent issues
                    issueNumbersToTry.AddRange(Enumerable.Range(latestIssueNumber - 19, 20));
                    
                    // Sample throughout the issue range to get a good distribution
                    for (int i = 0; i <= 10; i++)
                    {
                        // Distribute sampling points evenly across the issue range
                        int issueNumber = (int)(latestIssueNumber * (i / 10.0));
                        if (issueNumber > 0)
                            issueNumbersToTry.Add(issueNumber);
                    }
                    
                    // Add some random samples for additional coverage
                    Random random = new Random();
                    for (int i = 0; i < 10; i++)
                    {
                        int randomIssue = random.Next(1, latestIssueNumber);
                        issueNumbersToTry.Add(randomIssue);
                    }
                }
            }
            else
            {
                // Fallback if we couldn't determine the latest issue
                issueNumbersToTry.AddRange(Enumerable.Range(1, 50));
            }
            
            // List of common PR-like titles to filter out
            var prLikeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "dev", "development", "feature", "update", "fix", "merge", "release", 
                "ci", "bugfix", "hotfix", "refactor" 
            };
            
            int foundUserIssueCount = 0;
            
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
                        
                    // Skip PRs - more robust check
                    if (issue.PullRequest != null)
                    {
                        Log.Logger.Debug("Skipping PR #{Number}", issue.Number);
                        continue;
                    }
                    
                    // For "all" state, filter out PR-like issues to ensure we're getting true user issues
                    // Only do strong filtering if we haven't found any real user issues yet
                    bool skipPrLikeIssue = false;
                    if (state == "all" && foundUserIssueCount == 0 && prLikeTitles.Contains(issue.Title?.Trim()))
                    {
                        Log.Logger.Debug("Skipping likely PR-related issue #{Number}: {Title}", issue.Number, issue.Title);
                        skipPrLikeIssue = true;
                        
                        // Double-check with search API if this is a real issue
                        try 
                        {
                            string searchQuery = $"repo:{owner}/{repo} is:issue number:{issue.Number}";
                            var searchUrl = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(searchQuery)}";
                            var searchResponse = await _httpClient.GetAsync(searchUrl);
                            
                            if (searchResponse.IsSuccessStatusCode)
                            {
                                var searchContent = await searchResponse.Content.ReadAsStringAsync();
                                var searchResult = JsonSerializer.Deserialize<GitHubSearchResult>(searchContent,
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                    
                                if (searchResult?.TotalCount > 0 && searchResult?.Items?.Count > 0)
                                {
                                    // It's a real issue according to the search API
                                    skipPrLikeIssue = false;
                                    Log.Logger.Debug("Search API confirms #{Number} is a true issue, including it despite PR-like title", issue.Number);
                                }
                            }
                        }
                        catch {}
                        
                        if (skipPrLikeIssue)
                            continue;
                    }
                    
                    // Double-check if this is really an issue using search API when requesting just 1 issue
                    if (maxIssues == 1)
                    {
                        string searchQuery = $"repo:{owner}/{repo} is:issue number:{issue.Number}";
                        var searchUrl = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(searchQuery)}";
                        var searchResponse = await _httpClient.GetAsync(searchUrl);
                        
                        if (searchResponse.IsSuccessStatusCode)
                        {
                            var searchContent = await searchResponse.Content.ReadAsStringAsync();
                            var searchResult = JsonSerializer.Deserialize<GitHubSearchResult>(searchContent,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                
                            // If search API returns no results, this isn't a true issue
                            if (searchResult?.TotalCount == 0 || searchResult?.Items == null || !searchResult.Items.Any())
                            {
                                Log.Logger.Debug("Item #{Number} is not a true issue, skipping", issue.Number);
                                continue;
                            }
                            else
                            {
                                Log.Logger.Debug("Confirmed #{Number} is a true issue via search API", issue.Number);
                            }
                        }
                    }
                    
                    // If issue doesn't match the requested state, skip it
                    if (state != "all" && issue.State?.ToLowerInvariant() != state.ToLowerInvariant())
                    {
                        Log.Logger.Debug("Skipping issue #{Number} with state {State} as it doesn't match requested state {RequestedState}", 
                            issue.Number, issue.State, state);
                        continue;
                    }
                    
                    // Check if this is a user-created issue rather than a PR-related note
                    bool isLikelyUserIssue = false;
                    
                    // Check title for common user issue patterns
                    if (!prLikeTitles.Contains(issue.Title?.Trim()) && 
                        (issue.Title?.Contains("cannot", StringComparison.OrdinalIgnoreCase) == true ||
                         issue.Title?.Contains("bug", StringComparison.OrdinalIgnoreCase) == true ||
                         issue.Title?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ||
                         issue.Title?.Contains("issue", StringComparison.OrdinalIgnoreCase) == true ||
                         issue.Title?.Contains("problem", StringComparison.OrdinalIgnoreCase) == true ||
                         issue.Title?.Contains("crash", StringComparison.OrdinalIgnoreCase) == true ||
                         issue.Title?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true ||
                         issue.Title?.Contains("request", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        isLikelyUserIssue = true;
                    }
                    
                    // If creator isn't the repo owner, it's more likely a real user issue
                    if (issue.User?.Login?.Equals(owner, StringComparison.OrdinalIgnoreCase) == false)
                    {
                        isLikelyUserIssue = true;
                    }
                    
                    // If it has a detailed description, it's more likely a real issue
                    if (!string.IsNullOrWhiteSpace(issue.Body) && issue.Body.Length > 50)
                    {
                        isLikelyUserIssue = true;
                    }
                    
                    // If we found a likely user issue, increment our counter
                    if (isLikelyUserIssue)
                    {
                        foundUserIssueCount++;
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
        public GitHubApiUser? User { get; set; }
    }
    
    // Model for GitHub Search API response
    private class GitHubSearchResult
    {
        public int TotalCount { get; set; }
        public bool IncompleteResults { get; set; }
        public List<GitHubApiIssue> Items { get; set; } = new List<GitHubApiIssue>();
    }
    
    private class GitHubApiLabel
    {
        public string? Name { get; set; }
    }
    
    private class GitHubApiPullRequest
    {
        public string? Url { get; set; }
    }
    
    private class GitHubApiUser
    {
        public string? Login { get; set; }
    }
} 