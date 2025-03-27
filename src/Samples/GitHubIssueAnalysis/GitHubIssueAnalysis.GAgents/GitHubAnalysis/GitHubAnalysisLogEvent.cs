using Aevatar.Core.Abstractions;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis;

[GenerateSerializer]
public class GitHubAnalysisLogEvent : StateLogEventBase<GitHubAnalysisLogEvent>
{
    [Id(0)] public required string LogMessage { get; set; }
    [Id(1)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
} 