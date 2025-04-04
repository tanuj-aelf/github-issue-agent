using System;
using Orleans;

namespace GitHubIssueAnalysis.GAgents.GrainInterfaces.Models
{
    /// <summary>
    /// Represents a comprehensive analysis report for a GitHub repository
    /// </summary>
    [GenerateSerializer]
    public class RepositorySummaryReport
    {
        /// <summary>
        /// The name of the repository
        /// </summary>
        [Id(0)]
        public string Repository { get; set; }
        
        /// <summary>
        /// When the report was generated
        /// </summary>
        [Id(1)]
        public DateTime GeneratedAt { get; set; }
        
        /// <summary>
        /// Total number of issues in the repository
        /// </summary>
        [Id(2)]
        public int TotalIssues { get; set; }
        
        /// <summary>
        /// Number of open issues
        /// </summary>
        [Id(3)]
        public int OpenIssues { get; set; }
        
        /// <summary>
        /// Number of closed issues
        /// </summary>
        [Id(4)]
        public int ClosedIssues { get; set; }
        
        /// <summary>
        /// Date of the oldest issue
        /// </summary>
        [Id(5)]
        public DateTime OldestIssueDate { get; set; }
        
        /// <summary>
        /// Date of the newest issue
        /// </summary>
        [Id(6)]
        public DateTime NewestIssueDate { get; set; }
        
        /// <summary>
        /// Top tags extracted from issues
        /// </summary>
        [Id(7)]
        public TagStatistic[] TopTags { get; set; }
        
        /// <summary>
        /// Issue activity over time
        /// </summary>
        [Id(8)]
        public TimeRangeStatistic[] TimeRanges { get; set; }
        
        /// <summary>
        /// Actionable recommendations based on issue analysis
        /// </summary>
        [Id(9)]
        public IssueRecommendation[] Recommendations { get; set; }
    }
    
    /// <summary>
    /// Statistics for a specific tag
    /// </summary>
    [GenerateSerializer]
    public class TagStatistic
    {
        /// <summary>
        /// The tag name
        /// </summary>
        [Id(0)]
        public string Tag { get; set; }
        
        /// <summary>
        /// How many times this tag appears
        /// </summary>
        [Id(1)]
        public int Count { get; set; }
    }
    
    /// <summary>
    /// Statistics for issues in a specific time range
    /// </summary>
    [GenerateSerializer]
    public class TimeRangeStatistic
    {
        /// <summary>
        /// Start date of the time range
        /// </summary>
        [Id(0)]
        public DateTime StartDate { get; set; }
        
        /// <summary>
        /// End date of the time range
        /// </summary>
        [Id(1)]
        public DateTime EndDate { get; set; }
        
        /// <summary>
        /// Number of issues created during this time range
        /// </summary>
        [Id(2)]
        public int IssuesCreated { get; set; }
        
        /// <summary>
        /// Number of issues closed during this time range
        /// </summary>
        [Id(3)]
        public int IssuesClosed { get; set; }
    }
    
    /// <summary>
    /// An actionable recommendation based on issue analysis
    /// </summary>
    [GenerateSerializer]
    public class IssueRecommendation
    {
        /// <summary>
        /// Title of the recommendation
        /// </summary>
        [Id(0)]
        public string Title { get; set; }
        
        /// <summary>
        /// Detailed description of the recommendation
        /// </summary>
        [Id(1)]
        public string Description { get; set; }
        
        /// <summary>
        /// Priority level of the recommendation
        /// </summary>
        [Id(2)]
        public Priority Priority { get; set; }
        
        /// <summary>
        /// List of issue IDs that support this recommendation
        /// </summary>
        [Id(3)]
        public string[] SupportingIssues { get; set; }
    }
    
    /// <summary>
    /// Priority level for recommendations
    /// </summary>
    [GenerateSerializer]
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