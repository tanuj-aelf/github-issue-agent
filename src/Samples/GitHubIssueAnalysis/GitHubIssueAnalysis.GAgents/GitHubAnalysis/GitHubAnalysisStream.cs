using System;

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
        public const string StreamNamespace = "27F25E7A-1256-40A2-97F2-F8E8342104CB"; // Use a GUID string for Orleans compatibility
        
        /// <summary>
        /// The key used for the stream handling tags events
        /// </summary>
        public const string TagsStreamKey = "B7D8A935-9BFB-4C5D-A5D8-F353F6838A01"; // Use a GUID string for Orleans compatibility
        
        /// <summary>
        /// The key used for the stream handling summary report events
        /// </summary>
        public const string SummaryStreamKey = "E4D2A9F5-7D8C-41A6-9F3B-5D82C4F72E91"; // Use a GUID string for Orleans compatibility
    }
} 