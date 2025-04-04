using System;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Represents an event containing GitHub issue information
    /// </summary>
    [Serializable]
    public class GitHubIssueEvent
    {
        /// <summary>
        /// The issue information
        /// </summary>
        public GitHubIssueInfo IssueInfo { get; set; }
        
        /// <summary>
        /// The time the event was created
        /// </summary>
        public DateTime EventTime { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// The type of event (e.g., created, updated, closed)
        /// </summary>
        public string EventType { get; set; }
    }
} 