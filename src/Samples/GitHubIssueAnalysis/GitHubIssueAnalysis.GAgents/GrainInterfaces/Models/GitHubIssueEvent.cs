using System;
using Orleans;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Represents an event containing GitHub issue information
    /// </summary>
    [GenerateSerializer]
    public class GitHubIssueEvent
    {
        /// <summary>
        /// The issue information
        /// </summary>
        [Id(0)]
        public GitHubIssueInfo IssueInfo { get; set; }
        
        /// <summary>
        /// The time the event was created
        /// </summary>
        [Id(1)]
        public DateTime EventTime { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// The type of event (e.g., created, updated, closed)
        /// </summary>
        [Id(2)]
        public string EventType { get; set; }
    }
} 