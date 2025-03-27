using Orleans;

namespace GitHubIssueAnalysis.GAgents.Common;

[GenerateSerializer]
public class GitHubIssueInfo
{
    [Id(0)] public required string Id { get; set; }
    [Id(1)] public required string Title { get; set; }
    [Id(2)] public required string Description { get; set; }
    [Id(3)] public required string[] Labels { get; set; } = Array.Empty<string>();
    [Id(4)] public required string Url { get; set; }
    [Id(5)] public required string Repository { get; set; }
    [Id(6)] public DateTime CreatedAt { get; set; }
    [Id(7)] public required string Status { get; set; }
} 