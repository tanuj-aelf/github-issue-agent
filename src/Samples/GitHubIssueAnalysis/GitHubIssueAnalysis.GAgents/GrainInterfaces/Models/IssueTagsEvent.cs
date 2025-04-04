using System;
using Orleans;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Event published when tags have been extracted for an issue
    /// </summary>
    [GenerateSerializer]
    public class IssueTagsEvent
    {
        /// <summary>
        /// The repository containing the issue
        /// </summary>
        [Id(0)]
        public string Repository { get; set; }
        
        /// <summary>
        /// ID of the issue
        /// </summary>
        [Id(1)]
        public string IssueId { get; set; }
        
        /// <summary>
        /// Title of the issue
        /// </summary>
        [Id(2)]
        public string Title { get; set; }
        
        /// <summary>
        /// The array of extracted tags
        /// </summary>
        [Id(3)]
        public string[] ExtractedTags { get; set; }
        
        /// <summary>
        /// When the tags were extracted
        /// </summary>
        [Id(4)]
        public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
    }
} 