using Microsoft.Extensions.Logging;

namespace GitHubIssueAnalysis.GAgents.Services;

/// <summary>
/// A fallback LLM service that returns static responses when no API key is provided
/// </summary>
public class FallbackLLMService : ILLMService
{
    private readonly ILogger<FallbackLLMService> _logger;
    private readonly Random _random = new();

    public FallbackLLMService(ILogger<FallbackLLMService> logger)
    {
        _logger = logger;
    }

    public Task<string> CompletePromptAsync(string prompt)
    {
        _logger.LogWarning("========================================================================================");
        _logger.LogWarning("USING FALLBACK LLM SERVICE as no API key was provided - this is just simulated AI output");
        _logger.LogWarning("========================================================================================");

        if (prompt.Contains("extract relevant tags"))
        {
            var tags = GenerateRandomTags();
            _logger.LogWarning("FallbackLLMService generated tags: {Tags}", tags);
            return Task.FromResult(tags);
        }
        else if (prompt.Contains("provide strategic recommendations"))
        {
            var recommendations = GenerateRandomRecommendations();
            _logger.LogWarning("FallbackLLMService generated recommendations: {Recommendations}", recommendations);
            return Task.FromResult(recommendations);
        }

        _logger.LogWarning("FallbackLLMService returning generic message");
        return Task.FromResult("AI analysis is not available without an API key. Please configure a valid OpenAI API key.");
    }

    private string GenerateRandomTags()
    {
        var allTags = new[]
        {
            "bug", "feature", "enhancement", "documentation", "performance", "security", 
            "ux", "ui", "accessibility", "api", "backend", "frontend", "mobile", 
            "testing", "ci/cd", "infrastructure", "database", "analytics", "refactoring", 
            "tech-debt", "high-priority", "low-priority", "medium-priority"
        };

        var selectedTags = allTags
            .OrderBy(_ => _random.Next())
            .Take(_random.Next(5, 10))
            .ToArray();

        return string.Join(", ", selectedTags);
    }

    private string GenerateRandomRecommendations()
    {
        var allRecommendations = new[]
        {
            "Address critical bugs reported in the issue tracker to improve system stability",
            "Improve performance in core functionality areas that are frequently mentioned in issues",
            "Update documentation to address common questions and reduce support burden",
            "Implement most requested feature enhancements to improve user experience",
            "Focus on security issues as they represent significant risk to the application",
            "Prioritize accessibility improvements to make the application more inclusive",
            "Refactor code areas that are frequently mentioned in bug reports",
            "Invest in automated testing to prevent regression issues",
            "Improve error handling and user feedback mechanisms",
            "Consolidate similar feature requests into coherent implementation plans"
        };

        var selectedRecommendations = allRecommendations
            .OrderBy(_ => _random.Next())
            .Take(_random.Next(3, 5))
            .ToArray();

        return string.Join("\n", selectedRecommendations);
    }
} 