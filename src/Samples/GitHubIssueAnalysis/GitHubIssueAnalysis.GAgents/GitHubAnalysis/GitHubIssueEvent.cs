using Aevatar.Core.Abstractions;
using GitHubIssueAnalysis.GAgents.Common;
using Orleans;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

[GenerateSerializer]
public class GitHubIssueEvent : EventBase
{
    [Id(0)] public required GitHubIssueInfo IssueInfo { get; set; }
}

[GenerateSerializer]
public class IssueTagsEvent : EventBase
{
    [Id(0)] public required string IssueId { get; set; }
    [Id(1)] public required string Title { get; set; }
    [Id(2)] public required string[] ExtractedTags { get; set; } = Array.Empty<string>();
    [Id(3)] public required string Repository { get; set; }
}

[GenerateSerializer]
public class SummaryReportEvent : EventBase
{
    [Id(0)] public required string Repository { get; set; }
    [Id(1)] public required Dictionary<string, int> TagFrequency { get; set; } = new();
    [Id(2)] public required List<string> PriorityRecommendations { get; set; } = new();
    [Id(3)] public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    [Id(4)] public int TotalIssuesAnalyzed { get; set; }
} 