namespace GitHubIssueAnalysis.GAgents.Services;

/// <summary>
/// Configuration options for Azure OpenAI service
/// </summary>
public class AzureOpenAIOptions
{
    /// <summary>
    /// The Azure OpenAI API key
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The Azure OpenAI endpoint (without trailing slash)
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The deployment name for the model
    /// </summary>
    public string DeploymentName { get; set; } = string.Empty;

    /// <summary>
    /// The model name (e.g., gpt-4o, gpt-4o-mini, gpt-35-turbo)
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// The API version to use
    /// </summary>
    public string ApiVersion { get; set; } = "2024-02-15-preview";
} 