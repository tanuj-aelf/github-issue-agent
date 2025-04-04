using System;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Event published when tags have been extracted for an issue
    /// </summary>
    [Serializable]
    public class IssueTagsEvent
    {
        /// <summary>
        /// The repository containing the issue
        /// </summary>
        public string Repository { get; set; }
        
        /// <summary>
        /// ID of the issue
        /// </summary>
        public string IssueId { get; set; }
        
        /// <summary>
        /// Title of the issue
        /// </summary>
        public string Title { get; set; }
        
        /// <summary>
        /// The array of extracted tags
        /// </summary>
        public string[] ExtractedTags { get; set; }
        
        /// <summary>
        /// When the tags were extracted
        /// </summary>
        public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
    }
} 