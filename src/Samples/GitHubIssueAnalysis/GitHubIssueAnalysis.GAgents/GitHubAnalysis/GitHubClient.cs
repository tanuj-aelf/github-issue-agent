using GitHubIssueAnalysis.GAgents.Common;
using Octokit;
using Serilog;
using System.Threading.Tasks;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

public class GitHubClient
{
    private readonly Octokit.GitHubClient _client;

    public GitHubClient(string personalAccessToken)
    {
        _client = new Octokit.GitHubClient(new ProductHeaderValue("AevatarGitHubAnalyzer"));
        if (!string.IsNullOrEmpty(personalAccessToken))
        {
            _client.Credentials = new Credentials(personalAccessToken);
        }
    }

    public async Task<List<GitHubIssueInfo>> GetRepositoryIssuesAsync(string owner, string repo, int maxIssues = 100)
    {
        try
        {
            Log.Logger.Information("Starting GitHub issue analysis for {Owner}/{Repo}", owner, repo);
            
            var issues = new List<GitHubIssueInfo>();
            
            // First check if repository exists
            try 
            {
                await _client.Repository.Get(owner, repo);
                Log.Logger.Information("Repository exists, proceeding with analysis");
            }
            catch (NotFoundException)
            {
                Log.Logger.Error("Repository {Owner}/{Repo} not found", owner, repo);
                return issues;
            }
            
            // Octokit has issues with overflow exceptions, so we'll use direct API calls
            // and manually filter the PRs to get real issues
            
            // Try to get OPEN issues first (these are the ones we actually want)
            Log.Logger.Information("Attempting to fetch OPEN issues...");
            await GetOpenIssuesAsync(owner, repo, issues, maxIssues);
            
            // If we need more, try closed issues
            if (issues.Count < maxIssues)
            {
                Log.Logger.Information("Found {Count} open issues, attempting to get closed issues to reach limit of {Max}", 
                    issues.Count, maxIssues);
                
                await GetClosedIssuesAsync(owner, repo, issues, maxIssues);
            }
            
            // If we still don't have enough, try the one-by-one approach as a last resort
            if (issues.Count < maxIssues / 2)
            {
                Log.Logger.Information("Still only have {Count} issues, trying one-by-one approach", issues.Count);
                await FetchIssuesOneByOneAsync(owner, repo, issues, maxIssues);
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
    
    private async Task<int> GetOpenIssuesAsync(string owner, string repo, List<GitHubIssueInfo> issues, int maxIssues)
    {
        Log.Logger.Information("Fetching open issues with state filter = {State}", ItemState.Open);
        int fetchedCount = 0;

        try
        {
            // Try with the search API first
            try
            {
                var request = new SearchIssuesRequest
                {
                    State = ItemState.Open,
                    Type = IssueTypeQualifier.Issue,
                    Repos = new RepositoryCollection { $"{owner}/{repo}" }
                };

                var searchResults = await _client.Search.SearchIssues(request);
                
                foreach (var issue in searchResults.Items.Take(maxIssues))
                {
                    // Skip if it's a pull request
                    if (issue.PullRequest != null)
                        continue;

                    issues.Add(new GitHubIssueInfo
                    {
                        Id = issue.Number.ToString(),
                        Title = issue.Title,
                        Description = issue.Body ?? string.Empty,
                        Status = "open",
                        CreatedAt = issue.CreatedAt.DateTime,
                        Labels = issue.Labels.Select(l => l.Name).ToArray(),
                        Url = issue.HtmlUrl,
                        Repository = $"{owner}/{repo}"
                    });
                    
                    fetchedCount++;
                    
                    if (fetchedCount >= maxIssues)
                        break;
                }
                
                Log.Logger.Information("Successfully retrieved {Count} open issues via Search API", fetchedCount);
                return fetchedCount;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Error using Search API, falling back to direct API for open issues");
            }

            // Fallback to direct API if search fails
            try
            {
                var apiOptions = new ApiOptions
                {
                    PageSize = 100,
                    PageCount = 1
                };

                var request = new RepositoryIssueRequest
                {
                    State = ItemStateFilter.Open,
                    Filter = IssueFilter.All,
                    SortDirection = SortDirection.Descending
                };

                var openIssues = await _client.Issue.GetAllForRepository(owner, repo, request, apiOptions);
                
                foreach (var issue in openIssues.Take(maxIssues))
                {
                    // Skip if it's a pull request
                    if (issue.PullRequest != null)
                        continue;

                    issues.Add(new GitHubIssueInfo
                    {
                        Id = issue.Number.ToString(),
                        Title = issue.Title,
                        Description = issue.Body ?? string.Empty,
                        Status = "open",
                        CreatedAt = issue.CreatedAt.DateTime,
                        Labels = issue.Labels.Select(l => l.Name).ToArray(),
                        Url = issue.HtmlUrl,
                        Repository = $"{owner}/{repo}"
                    });
                    
                    fetchedCount++;
                    
                    if (fetchedCount >= maxIssues)
                        break;
                }
            }
            catch (OverflowException oex)
            {
                Log.Logger.Error(oex, "Overflow exception while fetching open issues");
                
                // Try one more time with a more restricted request to avoid the overflow
                try
                {
                    Log.Logger.Information("Attempting to fetch open issues with safer parameters");
                    var request = new RepositoryIssueRequest
                    {
                        State = ItemStateFilter.Open,
                        Filter = IssueFilter.All,
                        SortDirection = SortDirection.Descending
                    };

                    // Use the For method without pagination to avoid the overflow
                    var singleIssueList = await _client.Issue.GetAllForRepository(owner, repo, request);
                    
                    // Process only real issues
                    foreach (var issue in singleIssueList.Where(i => i.PullRequest == null).Take(maxIssues))
                    {
                        issues.Add(new GitHubIssueInfo
                        {
                            Id = issue.Number.ToString(),
                            Title = issue.Title,
                            Description = issue.Body ?? string.Empty,
                            Status = "open",
                            CreatedAt = issue.CreatedAt.DateTime,
                            Labels = issue.Labels.Select(l => l.Name).ToArray(),
                            Url = issue.HtmlUrl,
                            Repository = $"{owner}/{repo}"
                        });
                        
                        fetchedCount++;
                        
                        if (fetchedCount >= maxIssues)
                            break;
                    }
                }
                catch (Exception finalEx)
                {
                    Log.Logger.Error(finalEx, "Final attempt to fetch open issues failed");
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error fetching open issues with direct API");
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Unexpected error fetching open issues");
        }

        return fetchedCount;
    }
    
    private async Task<int> GetClosedIssuesAsync(string owner, string repo, List<GitHubIssueInfo> issues, int maxIssues)
    {
        Log.Logger.Information("Fetching closed issues with state filter = {State}", ItemState.Closed);
        int fetchedCount = 0;

        try
        {
            // Try with the search API first
            try
            {
                var request = new SearchIssuesRequest
                {
                    State = ItemState.Closed,
                    Type = IssueTypeQualifier.Issue,
                    Repos = new RepositoryCollection { $"{owner}/{repo}" }
                };

                var searchResults = await _client.Search.SearchIssues(request);
                
                foreach (var issue in searchResults.Items.Take(maxIssues))
                {
                    // Skip if it's a pull request
                    if (issue.PullRequest != null)
                        continue;

                    issues.Add(new GitHubIssueInfo
                    {
                        Id = issue.Number.ToString(),
                        Title = issue.Title,
                        Description = issue.Body ?? string.Empty,
                        Status = "closed",
                        CreatedAt = issue.CreatedAt.DateTime,
                        Labels = issue.Labels.Select(l => l.Name).ToArray(),
                        Url = issue.HtmlUrl,
                        Repository = $"{owner}/{repo}"
                    });
                    
                    fetchedCount++;
                    
                    if (fetchedCount >= maxIssues)
                        break;
                }
                
                Log.Logger.Information("Successfully retrieved {Count} closed issues via Search API", fetchedCount);
                return fetchedCount;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Error using Search API, falling back to direct API for closed issues");
            }

            // Fallback to direct API if search fails
            try
            {
                var apiOptions = new ApiOptions
                {
                    PageSize = 100,
                    PageCount = 1
                };

                var request = new RepositoryIssueRequest
                {
                    State = ItemStateFilter.Closed,
                    Filter = IssueFilter.All,
                    SortDirection = SortDirection.Descending
                };

                var closedIssues = await _client.Issue.GetAllForRepository(owner, repo, request, apiOptions);
                
                foreach (var issue in closedIssues.Take(maxIssues))
                {
                    // Skip if it's a pull request
                    if (issue.PullRequest != null)
                        continue;

                    issues.Add(new GitHubIssueInfo
                    {
                        Id = issue.Number.ToString(),
                        Title = issue.Title,
                        Description = issue.Body ?? string.Empty,
                        Status = "closed",
                        CreatedAt = issue.CreatedAt.DateTime,
                        Labels = issue.Labels.Select(l => l.Name).ToArray(),
                        Url = issue.HtmlUrl,
                        Repository = $"{owner}/{repo}"
                    });
                    
                    fetchedCount++;
                    
                    if (fetchedCount >= maxIssues)
                        break;
                }
            }
            catch (OverflowException oex)
            {
                Log.Logger.Error(oex, "Overflow exception while fetching closed issues");
                
                // Try one more time with a more restricted request to avoid the overflow
                try
                {
                    Log.Logger.Information("Attempting to fetch closed issues with safer parameters");
                    var request = new RepositoryIssueRequest
                    {
                        State = ItemStateFilter.Closed,
                        Filter = IssueFilter.All,
                        SortDirection = SortDirection.Descending
                    };

                    // Use the For method without pagination to avoid the overflow
                    var singleIssueList = await _client.Issue.GetAllForRepository(owner, repo, request);
                    
                    // Process only real issues
                    foreach (var issue in singleIssueList.Where(i => i.PullRequest == null).Take(maxIssues))
                    {
                        issues.Add(new GitHubIssueInfo
                        {
                            Id = issue.Number.ToString(),
                            Title = issue.Title,
                            Description = issue.Body ?? string.Empty,
                            Status = "closed",
                            CreatedAt = issue.CreatedAt.DateTime,
                            Labels = issue.Labels.Select(l => l.Name).ToArray(),
                            Url = issue.HtmlUrl,
                            Repository = $"{owner}/{repo}"
                        });
                        
                        fetchedCount++;
                        
                        if (fetchedCount >= maxIssues)
                            break;
                    }
                }
                catch (Exception finalEx)
                {
                    Log.Logger.Error(finalEx, "Final attempt to fetch closed issues failed");
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error fetching closed issues with direct API");
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Unexpected error fetching closed issues");
        }

        return fetchedCount;
    }

    private async Task FetchIssuesOneByOneAsync(string owner, string repo, List<GitHubIssueInfo> issues, int maxIssues)
    {
        try
        {
            Log.Logger.Information("Resorting to fetching issues one-by-one (last resort)");
            
            // We'll try issues with different number ranges to maximize our chances
            // of finding actual issues, not just PRs
            
            // Get list of existing issue IDs to avoid duplicates
            var existingIds = issues.Select(i => i.Id).ToHashSet();
            
            // The usual approach is to check sequential IDs, but that's inefficient.
            // Let's try a smarter approach:
            
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
                    var issue = await _client.Issue.Get(owner, repo, issueNumber);
                    
                    // Skip PRs
                    if (issue.PullRequest != null)
                    {
                        Log.Logger.Debug("Skipping PR #{Number}", issue.Number);
                        continue;
                    }
                    
                    // Always prefer open issues
                    if (issue.State.Value == ItemState.Closed && issues.Count > maxIssues / 2)
                    {
                        // Skip closed issues if we already have enough
                        Log.Logger.Debug("Skipping closed issue #{Number} as we already have enough issues", issue.Number);
                        continue;
                    }
                    
                    var issueInfo = new GitHubIssueInfo
                    {
                        Id = issue.Number.ToString(),
                        Title = issue.Title ?? "Untitled Issue",
                        Description = issue.Body ?? string.Empty,
                        Labels = issue.Labels?.Select(l => l.Name)?.ToArray() ?? Array.Empty<string>(),
                        Url = issue.HtmlUrl ?? $"https://github.com/{owner}/{repo}/issues/{issue.Number}",
                        Repository = $"{owner}/{repo}",
                        CreatedAt = issue.CreatedAt.DateTime,
                        Status = issue.State.StringValue
                    };
                    
                    issues.Add(issueInfo);
                    existingIds.Add(issue.Number.ToString());
                    
                    // Log details
                    if (issue.Labels?.Count > 0)
                    {
                        Log.Logger.Information("Issue #{Number} ({Status}) has {Count} labels: {Labels}", 
                            issue.Number, issue.State.StringValue, issue.Labels.Count, 
                            string.Join(", ", issue.Labels.Select(l => l.Name)));
                    }
                    else
                    {
                        Log.Logger.Information("Issue #{Number} ({Status}) has no labels", 
                            issue.Number, issue.State.StringValue);
                    }
                    
                    Log.Logger.Information("Found individual issue #{Number}: {Title} ({Status})", 
                        issue.Number, issue.Title, issue.State.StringValue);
                    
                    if (issues.Count >= maxIssues)
                        break;
                }
                catch (NotFoundException)
                {
                    // Issue number doesn't exist, skip silently
                }
                catch (Exception ex)
                {
                    if (ex is OverflowException || 
                        ex.InnerException is OverflowException || 
                        ex.Message.Contains("Overflow") || 
                        (ex.InnerException?.Message?.Contains("Overflow") ?? false))
                    {
                        Log.Logger.Debug("Overflow error for issue #{Number}, skipping", issueNumber);
                    }
                    else
                    {
                        Log.Logger.Error(ex, "Error fetching issue #{Number}", issueNumber);
                    }
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
            // Try to get the latest issue or PR to determine the rough number range
            var latest = await _client.Issue.GetAllForRepository(owner, repo, new RepositoryIssueRequest
            {
                State = ItemStateFilter.All,
                SortDirection = SortDirection.Descending
            }, new ApiOptions { PageSize = 1, PageCount = 1 });
            
            return latest.FirstOrDefault()?.Number ?? 50; // Default to 50 if nothing found
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
            var issue = await _client.Issue.Get(owner, repo, issueNumber);
            
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
                Labels = issue.Labels?.Select(l => l.Name)?.ToArray() ?? Array.Empty<string>(),
                Url = issue.HtmlUrl ?? $"https://github.com/{owner}/{repo}/issues/{issue.Number}",
                Repository = $"{owner}/{repo}",
                CreatedAt = issue.CreatedAt.DateTime,
                Status = issue.State.StringValue
            };
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error fetching issue #{IssueNumber} for repository {Owner}/{Repo}", issueNumber, owner, repo);
            throw;
        }
    }

    public async Task<List<GitHubIssueInfo>> FetchIssuesAsync(string owner, string repo, int maxIssues)
    {
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                Log.Logger.Error("Invalid owner or repository name");
                return new List<GitHubIssueInfo>();
            }

            // Check if the repository exists
            try
            {
                await _client.Repository.Get(owner, repo);
                Log.Logger.Information("Repository exists, proceeding with analysis");
            }
            catch (NotFoundException)
            {
                Log.Logger.Error("Repository {Owner}/{Repo} not found", owner, repo);
                return new List<GitHubIssueInfo>();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error checking repository existence");
                return new List<GitHubIssueInfo>();
            }

            var issues = new List<GitHubIssueInfo>();

            // First try to get open issues
            Log.Logger.Information("Attempting to fetch OPEN issues...");
            int openIssuesCount = await GetOpenIssuesAsync(owner, repo, issues, maxIssues);
            
            // If we didn't get enough issues, try to get closed issues as well
            if (openIssuesCount < maxIssues)
            {
                int remainingIssues = maxIssues - openIssuesCount;
                Log.Logger.Information("Found {Count} open issues, attempting to get closed issues to reach limit of {Max}", openIssuesCount, maxIssues);
                await GetClosedIssuesAsync(owner, repo, issues, remainingIssues);
            }

            Log.Logger.Information("Analysis complete. Got {Count} issues from {Owner}/{Repo}", issues.Count, owner, repo);
            return issues;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error fetching issues for {Owner}/{Repo}", owner, repo);
            return new List<GitHubIssueInfo>();
        }
    }
} 