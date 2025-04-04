using System;
using Orleans;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Represents GitHub issue information
    /// </summary>
    [GenerateSerializer]
    public class GitHubIssueInfo
    {
        /// <summary>
        /// The unique identifier of the issue
        /// </summary>
        [Id(0)]
        public string Id { get; set; }
        
        /// <summary>
        /// The title of the issue
        /// </summary>
        [Id(1)]
        public string Title { get; set; }
        
        /// <summary>
        /// The description/body of the issue
        /// </summary>
        [Id(2)]
        public string Description { get; set; }
        
        /// <summary>
        /// The status of the issue (e.g., open, closed)
        /// </summary>
        [Id(3)]
        public string Status { get; set; }
        
        /// <summary>
        /// The state of the issue (e.g., open, closed)
        /// </summary>
        [Id(4)]
        public string State { get; set; }
        
        /// <summary>
        /// The URL of the issue
        /// </summary>
        [Id(5)]
        public string Url { get; set; }
        
        /// <summary>
        /// The repository containing the issue
        /// </summary>
        [Id(6)]
        public string Repository { get; set; }
        
        /// <summary>
        /// The time the issue was created
        /// </summary>
        [Id(7)]
        public DateTime CreatedAt { get; set; }
        
        /// <summary>
        /// The time the issue was last updated
        /// </summary>
        [Id(8)]
        public DateTime UpdatedAt { get; set; }
        
        /// <summary>
        /// The time the issue was closed (if applicable)
        /// </summary>
        [Id(9)]
        public DateTime? ClosedAt { get; set; }
        
        /// <summary>
        /// Labels applied to the issue in GitHub
        /// </summary>
        [Id(10)]
        public string[] Labels { get; set; } = Array.Empty<string>();
    }
} 