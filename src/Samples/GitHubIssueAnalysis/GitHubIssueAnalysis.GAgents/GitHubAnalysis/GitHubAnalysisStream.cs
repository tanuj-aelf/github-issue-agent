namespace GitHubIssueAnalysis.GAgents.GitHubAnalysis
{
    /// <summary>
    /// Constants for stream names and IDs used in the GitHub analysis system
    /// </summary>
    public static class GitHubAnalysisStream
    {
        /// <summary>
        /// The namespace for all GitHub analysis streams
        /// </summary>
        public const string StreamNamespace = "github-analysis";
        
        /// <summary>
        /// The key used for the stream handling tags events
        /// </summary>
        public const string TagsStreamKey = "tags-stream";
        
        /// <summary>
        /// The key used for the stream handling summary report events
        /// </summary>
        public const string SummaryStreamKey = "summary-stream";
    }
} 