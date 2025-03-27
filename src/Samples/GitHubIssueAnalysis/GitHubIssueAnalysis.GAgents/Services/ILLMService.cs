namespace GitHubIssueAnalysis.GAgents.Services;

/// <summary>
/// Interface for LLM (Large Language Model) service
/// </summary>
public interface ILLMService
{
    /// <summary>
    /// Completes a prompt using the configured LLM
    /// </summary>
    /// <param name="prompt">The prompt to complete</param>
    /// <returns>The completion response</returns>
    Task<string> CompletePromptAsync(string prompt);
} 