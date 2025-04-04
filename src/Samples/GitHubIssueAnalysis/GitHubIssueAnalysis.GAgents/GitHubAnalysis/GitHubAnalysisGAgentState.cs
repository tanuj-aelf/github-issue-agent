using System;
using System.Collections.Generic;
using GitHubIssueAnalysis.GAgents.GrainInterfaces.Models;
using Aevatar.Core.Abstractions;

namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis
{
    [Serializable]
    public class GitHubAnalysisGAgentState : StateBase
    {
        /// <summary>
        /// Dictionary mapping repository names to a list of issues in that repository
        /// </summary>
        public Dictionary<string, List<GitHubIssueInfo>> RepositoryIssues { get; set; } = new Dictionary<string, List<GitHubIssueInfo>>();

        /// <summary>
        /// Dictionary mapping repository names to a dictionary of issue IDs to their extracted tags
        /// </summary>
        public Dictionary<string, Dictionary<string, List<string>>> IssueTags { get; set; } = new Dictionary<string, Dictionary<string, List<string>>>();

        /// <summary>
        /// Dictionary mapping repository names to a list of summary reports generated for that repository
        /// </summary>
        public Dictionary<string, List<RepositorySummaryReport>> RepositorySummaries { get; set; } = new Dictionary<string, List<RepositorySummaryReport>>();
        
        /// <summary>
        /// Last time a repository was analyzed
        /// </summary>
        public Dictionary<string, DateTime> LastRepositoryAnalyzedTime { get; set; } = new Dictionary<string, DateTime>();
    }
} 