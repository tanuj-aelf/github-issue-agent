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
            Log.Logger.Information("Attempting to fetch issues for repository {Owner}/{Repo}", owner, repo);
            
            var issues = new List<GitHubIssueInfo>();
            
            // First check if repository exists
            try 
            {
                await _client.Repository.Get(owner, repo);
            }
            catch (NotFoundException)
            {
                Log.Logger.Error("Repository {Owner}/{Repo} not found", owner, repo);
                return issues;
            }
            
            // Instead of using the problematic GetAllForRepository method,
            // we'll manually fetch issues one by one using their ID
            
            // First, try to get the most recent issue to determine how many issues exist
            try
            {
                // Get a single issue just to check if repo has any issues
                var recentIssue = await _client.Issue.Get(owner, repo, 1);
                if (recentIssue != null)
                {
                    Log.Logger.Information("Repository has at least one issue");
                    
                    // Let's try to fetch up to maxIssues sequentially
                    // Start with the most recent issue numbers and work backwards
                    
                    // Get repository to check for issue count
                    var repository = await _client.Repository.Get(owner, repo);
                    int totalIssueCount = repository.OpenIssuesCount;
                    
                    Log.Logger.Information("Repository has approximately {Count} open issues", totalIssueCount);
                    
                    // Start with recent issues
                    var issuesFound = 0;
                    var attemptedIssues = 0;
                    
                    // Try to get the last 100 issues by ID, which should avoid the overflow issue
                    for (int i = 1; i <= 100 && issuesFound < maxIssues && attemptedIssues < 200; i++)
                    {
                        try
                        {
                            attemptedIssues++;
                            var issue = await _client.Issue.Get(owner, repo, i);
                            
                            issues.Add(new GitHubIssueInfo
                            {
                                Id = issue.Number.ToString(),
                                Title = issue.Title,
                                Description = issue.Body ?? string.Empty,
                                Labels = issue.Labels.Select(l => l.Name).ToArray(),
                                Url = issue.HtmlUrl,
                                Repository = $"{owner}/{repo}",
                                CreatedAt = issue.CreatedAt.DateTime,
                                Status = issue.State.StringValue
                            });
                            
                            issuesFound++;
                            Log.Logger.Debug("Retrieved issue #{IssueNumber} for repository {Owner}/{Repo}", i, owner, repo);
                        }
                        catch (NotFoundException)
                        {
                            // This issue number doesn't exist, skip it
                            continue;
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Error(ex, "Error fetching issue #{IssueNumber} for repository {Owner}/{Repo}", i, owner, repo);
                            // Continue trying other issues
                        }
                    }
                }
                else
                {
                    Log.Logger.Information("No issues found in repository {Owner}/{Repo}", owner, repo);
                }
            }
            catch (NotFoundException)
            {
                Log.Logger.Information("No issues found in repository {Owner}/{Repo}", owner, repo);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error checking for issues in repository {Owner}/{Repo}", owner, repo);
            }
            
            return issues;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error fetching issues for repository {Owner}/{Repo}", owner, repo);
            return new List<GitHubIssueInfo>();
        }
    }

    public async Task<GitHubIssueInfo> GetIssueAsync(string owner, string repo, int issueNumber)
    {
        try
        {
            var issue = await _client.Issue.Get(owner, repo, issueNumber);
            
            return new GitHubIssueInfo
            {
                Id = issue.Number.ToString(),
                Title = issue.Title,
                Description = issue.Body ?? string.Empty,
                Labels = issue.Labels.Select(l => l.Name).ToArray(),
                Url = issue.HtmlUrl,
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
} 