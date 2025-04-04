using System;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Represents GitHub issue information
    /// </summary>
    [Serializable]
    public class GitHubIssueInfo
    {
        /// <summary>
        /// The unique identifier of the issue
        /// </summary>
        public string Id { get; set; }
        
        /// <summary>
        /// The title of the issue
        /// </summary>
        public string Title { get; set; }
        
        /// <summary>
        /// The description/body of the issue
        /// </summary>
        public string Description { get; set; }
        
        /// <summary>
        /// The status of the issue (e.g., open, closed)
        /// </summary>
        public string Status { get; set; }
        
        /// <summary>
        /// The state of the issue (e.g., open, closed)
        /// </summary>
        public string State { get; set; }
        
        /// <summary>
        /// The URL of the issue
        /// </summary>
        public string Url { get; set; }
        
        /// <summary>
        /// The repository containing the issue
        /// </summary>
        public string Repository { get; set; }
        
        /// <summary>
        /// The time the issue was created
        /// </summary>
        public DateTime CreatedAt { get; set; }
        
        /// <summary>
        /// The time the issue was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; }
        
        /// <summary>
        /// The time the issue was closed (if applicable)
        /// </summary>
        public DateTime? ClosedAt { get; set; }
        
        /// <summary>
        /// Labels applied to the issue in GitHub
        /// </summary>
        public string[] Labels { get; set; } = Array.Empty<string>();
    }
} 