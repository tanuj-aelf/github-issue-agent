using Microsoft.Extensions.Logging;

namespace GitHubIssueAnalysis.GAgents.Services;

/// <summary>
/// A composite LLM service that tries multiple providers in sequence until one succeeds
/// </summary>
public class CompositeLLMService : ILLMService
{
    private readonly ILogger<CompositeLLMService> _logger;
    private readonly IEnumerable<ILLMService> _services;

    public CompositeLLMService(
        ILogger<CompositeLLMService> logger,
        IEnumerable<ILLMService> services)
    {
        _logger = logger;
        _services = services;
    }

    public async Task<string> CompletePromptAsync(string prompt)
    {
        _logger.LogWarning("CompositeLLMService trying {Count} LLM services in sequence", _services.Count());
        _logger.LogWarning("Available LLM services: {ServiceTypes}", 
            string.Join(", ", _services.Select(s => s.GetType().Name)));
        
        foreach (var service in _services)
        {
            try
            {
                _logger.LogWarning("Trying LLM service: {ServiceType}", service.GetType().Name);
                
                var result = await service.CompletePromptAsync(prompt);
                
                if (!string.IsNullOrWhiteSpace(result))
                {
                    _logger.LogWarning("Successfully got response from {ServiceType}", service.GetType().Name);
                    _logger.LogWarning("Response (first 100 chars): {ResponseStart}", 
                        result.Length > 100 ? result.Substring(0, 100) + "..." : result);
                    return result;
                }
                
                _logger.LogWarning("Service {ServiceType} returned empty result, trying next service", service.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error using LLM service {ServiceType}, trying next service", service.GetType().Name);
            }
        }
        
        _logger.LogError("All LLM services failed, returning empty result");
        return string.Empty;
    }
} 