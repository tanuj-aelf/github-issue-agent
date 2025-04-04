using System;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Represents a comprehensive analysis report for a GitHub repository
    /// </summary>
    [Serializable]
    public class RepositorySummaryReport
    {
        /// <summary>
        /// The name of the repository
        /// </summary>
        public string Repository { get; set; }
        
        /// <summary>
        /// When the report was generated
        /// </summary>
        public DateTime GeneratedAt { get; set; }
        
        /// <summary>
        /// Total number of issues in the repository
        /// </summary>
        public int TotalIssues { get; set; }
        
        /// <summary>
        /// Number of open issues
        /// </summary>
        public int OpenIssues { get; set; }
        
        /// <summary>
        /// Number of closed issues
        /// </summary>
        public int ClosedIssues { get; set; }
        
        /// <summary>
        /// Date of the oldest issue
        /// </summary>
        public DateTime OldestIssueDate { get; set; }
        
        /// <summary>
        /// Date of the newest issue
        /// </summary>
        public DateTime NewestIssueDate { get; set; }
        
        /// <summary>
        /// Top tags extracted from issues
        /// </summary>
        public TagStatistic[] TopTags { get; set; }
        
        /// <summary>
        /// Issue activity over time
        /// </summary>
        public TimeRangeStatistic[] TimeRanges { get; set; }
        
        /// <summary>
        /// Actionable recommendations based on issue analysis
        /// </summary>
        public IssueRecommendation[] Recommendations { get; set; }
    }
    
    /// <summary>
    /// Statistics for a specific tag
    /// </summary>
    [Serializable]
    public class TagStatistic
    {
        /// <summary>
        /// The tag name
        /// </summary>
        public string Tag { get; set; }
        
        /// <summary>
        /// How many times this tag appears
        /// </summary>
        public int Count { get; set; }
    }
    
    /// <summary>
    /// Statistics for issues in a specific time range
    /// </summary>
    [Serializable]
    public class TimeRangeStatistic
    {
        /// <summary>
        /// Start date of the time range
        /// </summary>
        public DateTime StartDate { get; set; }
        
        /// <summary>
        /// End date of the time range
        /// </summary>
        public DateTime EndDate { get; set; }
        
        /// <summary>
        /// Number of issues created during this time range
        /// </summary>
        public int IssuesCreated { get; set; }
        
        /// <summary>
        /// Number of issues closed during this time range
        /// </summary>
        public int IssuesClosed { get; set; }
    }
    
    /// <summary>
    /// An actionable recommendation based on issue analysis
    /// </summary>
    [Serializable]
    public class IssueRecommendation
    {
        /// <summary>
        /// Title of the recommendation
        /// </summary>
        public string Title { get; set; }
        
        /// <summary>
        /// Detailed description of the recommendation
        /// </summary>
        public string Description { get; set; }
        
        /// <summary>
        /// Priority level of the recommendation
        /// </summary>
        public Priority Priority { get; set; }
        
        /// <summary>
        /// List of issue IDs that support this recommendation
        /// </summary>
        public string[] SupportingIssues { get; set; }
    }
    
    /// <summary>
    /// Priority level for recommendations
    /// </summary>
    [Serializable]
    public enum Priority
    {
        /// <summary>
        /// High priority - should be addressed immediately
        /// </summary>
        High,
        
        /// <summary>
        /// Medium priority - should be addressed soon
        /// </summary>
        Medium,
        
        /// <summary>
        /// Low priority - can be addressed later
        /// </summary>
        Low
    }
} 