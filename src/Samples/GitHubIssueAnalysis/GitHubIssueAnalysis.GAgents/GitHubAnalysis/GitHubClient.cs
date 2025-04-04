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
            
            issueState = issueState.ToLowerInvariant();
            
            if (issueState != "open" && issueState != "closed" && issueState != "all")
            {
                Log.Logger.Warning("Invalid state parameter: {State}, defaulting to 'all'", issueState);
                issueState = "all";
            }
            
            if (maxIssues < 5 && issueState != "all")
            {
                Log.Logger.Warning("Maximum issue count ({MaxIssues}) is very low. Consider increasing to get a better sample.", maxIssues);
            }
            
            if (maxIssues == 1 && issueState == "closed")
            {
                Log.Logger.Information("Attempting to find a single user-reported closed issue...");
                
                await GetIssuesDirectApiAsync(owner, repo, issues, maxIssues, issueState);
                
                if (issues.Count == 0)
                {
                    Log.Logger.Information("No suitable closed issues found via API call, trying extended search");
                    
                    try 
                    {
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
                
                if (issues.Count == 0)
                {
                    Log.Logger.Information("Still no issues found, trying one-by-one approach");
                    await FetchIssuesOneByOneAsync(owner, repo, issues, maxIssues, issueState);
                }
            }
            else
            {
                await GetIssuesDirectApiAsync(owner, repo, issues, maxIssues, issueState);
                
                if (issues.Count == 0 && issueState == "all")
                {
                    Log.Logger.Information("No issues found with 'all' state filter. Trying specific searches...");
                    
                    var closedIssues = new List<GitHubIssueInfo>();
                    await GetIssuesDirectApiAsync(owner, repo, closedIssues, maxIssues, "closed");
                    
                    if (closedIssues.Count > 0)
                    {
                        Log.Logger.Information("Found {Count} closed issues", closedIssues.Count);
                        issues.AddRange(closedIssues);
                    }
                    
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
                    
                    if (issues.Count == 0)
                    {
                        try
                        {
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
                            Log.Logger.Error(ex, "Error during basic search");
                        }
                    }
                    
                    if (issues.Count == 0)
                    {
                        try
                        {
                            Log.Logger.Information("Trying generic search for issues in repository");
                            
                            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/issues?per_page={maxIssues}&state=all";
                            Log.Logger.Information("Direct API URL: {Url}", apiUrl);
                            
                            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                            var response = await _httpClient.SendAsync(request);
                            
                            if (response.IsSuccessStatusCode)
                            {
                                var content = await response.Content.ReadAsStringAsync();
                                var apiIssues = JsonSerializer.Deserialize<List<GitHubApiIssue>>(content, 
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                
                                if (apiIssues != null && apiIssues.Count > 0)
                                {
                                    Log.Logger.Information("Generic search found {Count} items (may include PRs)", apiIssues.Count);
                                    
                                    foreach (var issue in apiIssues.Where(i => i.PullRequest == null).Take(maxIssues))
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
                            else
                            {
                                Log.Logger.Warning("Generic search request failed: {Status}", response.StatusCode);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Error(ex, "Error during generic issue search");
                        }
                    }
                }
            }
            
            return issues;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error in GetRepositoryIssuesAsync");
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
            var existingIds = issues.Select(i => i.Id).ToHashSet();
            
            bool hasMorePages = true;
            while (hasMorePages && fetchedCount < maxIssues)
            {
                string searchQuery;
                
                if (state == "all")
                {
                    searchQuery = $"repo:{owner}/{repo} is:issue -author:{owner} -label:enhancement -label:feature -title:dev -title:merge -title:update -title:fix";
                }
                else
                {
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
                
                Log.Logger.Information("Search API found {TotalCount} true issues matching the query", searchResult.TotalCount);
                
                Log.Logger.Information("Search API returned issue numbers: {Numbers}", 
                    string.Join(", ", searchResult.Items.Select(i => i.Number)));
                
                foreach (var issue in searchResult.Items)
                {
                    if (existingIds.Contains(issue.Number.ToString()))
                    {
                        Log.Logger.Debug("Skipping duplicate issue #{Number}", issue.Number);
                        continue;
                    }
                    
                    Log.Logger.Debug("Processing issue #{Number}: '{Title}' with state: {State}", 
                        issue.Number, issue.Title, issue.State);
                    
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
            
            var existingIds = issues.Select(i => i.Id).ToHashSet();
            
            int latestIssueNumber = await GetLatestIssueNumberAsync(owner, repo);
            Log.Logger.Information("Latest issue/PR number is approximately: {Number}", latestIssueNumber);
            
            var issueNumbersToTry = new List<int>();
            
            if (maxIssues == 1 || latestIssueNumber <= 20)
            {
                for (int i = 1; i <= Math.Min(15, latestIssueNumber); i++)
                {
                    issueNumbersToTry.Add(i);
                }
            }
            
            if (latestIssueNumber > 0)
            {
                if (latestIssueNumber <= 50)
                {
                    issueNumbersToTry.AddRange(Enumerable.Range(1, latestIssueNumber));
                }
                else if (latestIssueNumber <= 200)
                {
                    issueNumbersToTry.AddRange(Enumerable.Range(latestIssueNumber - 19, 20));
                    
                    int midpoint = latestIssueNumber / 2;
                    issueNumbersToTry.AddRange(Enumerable.Range(midpoint - 10, 20));
                    
                    for (int i = midpoint - 50; i > 0; i -= 10)
                    {
                        issueNumbersToTry.Add(i);
                    }
                }
                else
                {
                    issueNumbersToTry.AddRange(Enumerable.Range(latestIssueNumber - 19, 20));
                    
                    for (int i = 0; i <= 10; i++)
                    {
                        int issueNumber = (int)(latestIssueNumber * (i / 10.0));
                        if (issueNumber > 0)
                            issueNumbersToTry.Add(issueNumber);
                    }
                    
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
                issueNumbersToTry.AddRange(Enumerable.Range(1, 50));
            }
            
            var prLikeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "dev", "development", "feature", "update", "fix", "merge", "release", 
                "ci", "bugfix", "hotfix", "refactor" 
            };
            
            int foundUserIssueCount = 0;
            
            foreach (var issueNumber in issueNumbersToTry)
            {
                if (issues.Count >= maxIssues || existingIds.Contains(issueNumber.ToString()))
                    continue;
                
                try
                {
                    var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}");
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }
                    
                    var content = await response.Content.ReadAsStringAsync();
                    var issue = JsonSerializer.Deserialize<GitHubApiIssue>(content, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        
                    if (issue == null)
                        continue;
                        
                    if (issue.PullRequest != null)
                    {
                        Log.Logger.Debug("Skipping PR #{Number}", issue.Number);
                        continue;
                    }
                    
                    bool skipPrLikeIssue = false;
                    if (state == "all" && foundUserIssueCount == 0 && prLikeTitles.Contains(issue.Title?.Trim()))
                    {
                        Log.Logger.Debug("Skipping likely PR-related issue #{Number}: {Title}", issue.Number, issue.Title);
                        skipPrLikeIssue = true;
                        
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
                                    skipPrLikeIssue = false;
                                    Log.Logger.Debug("Search API confirms #{Number} is a true issue, including it despite PR-like title", issue.Number);
                                }
                            }
                        }
                        catch {}
                        
                        if (skipPrLikeIssue)
                            continue;
                    }
                    
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
                    
                    if (state != "all" && issue.State?.ToLowerInvariant() != state.ToLowerInvariant())
                    {
                        Log.Logger.Debug("Skipping issue #{Number} with state {State} as it doesn't match requested state {RequestedState}", 
                            issue.Number, issue.State, state);
                        continue;
                    }
                    
                    bool isLikelyUserIssue = false;
                    
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
                    
                    if (issue.User?.Login?.Equals(owner, StringComparison.OrdinalIgnoreCase) == false)
                    {
                        isLikelyUserIssue = true;
                    }
                    
                    if (!string.IsNullOrWhiteSpace(issue.Body) && issue.Body.Length > 50)
                    {
                        isLikelyUserIssue = true;
                    }
                    
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
            var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/issues?per_page=1&state=all");
            
            if (!response.IsSuccessStatusCode)
                return 50;
                
            var content = await response.Content.ReadAsStringAsync();
            var issues = JsonSerializer.Deserialize<List<GitHubApiIssue>>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
            var latestIssueNumber = issues?.FirstOrDefault()?.Number ?? 0;
            if (latestIssueNumber > int.MaxValue)
            {
                Log.Logger.Warning("Issue number {Number} exceeds int.MaxValue, using 1000 as a default", latestIssueNumber);
                return 1000;
            }
            
            return (int)latestIssueNumber;
        }
        catch
        {
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