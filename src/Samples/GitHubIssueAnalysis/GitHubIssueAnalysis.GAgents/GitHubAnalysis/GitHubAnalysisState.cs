using Aevatar.Core.Abstractions;
using GitHubIssueAnalysis.GAgents.Common;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

[GenerateSerializer]
public class GitHubAnalysisState : StateBase
{
    [Id(0)] public Dictionary<string, List<GitHubIssueInfo>> RepositoryIssues { get; set; } = new();
    [Id(1)] public Dictionary<string, Dictionary<string, List<string>>> IssueTags { get; set; } = new();
} 