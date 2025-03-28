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
            // Check if this is for a GitHub issue, and if so, try to extract context
            if (prompt.Contains("Title:") && prompt.Contains("Description:"))
            {
                // Try to extract some context from the prompt
                var tags = GenerateContextualTags(prompt);
                _logger.LogWarning("FallbackLLMService generated contextual tags: {Tags}", tags);
                return Task.FromResult(tags);
            }
            else
            {
                var tags = GenerateRandomTags();
                _logger.LogWarning("FallbackLLMService generated random tags: {Tags}", tags);
                return Task.FromResult(tags);
            }
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

    private string GenerateContextualTags(string prompt)
    {
        // Extract title and description from the prompt
        string title = "";
        string description = "";
        
        var titleMatch = System.Text.RegularExpressions.Regex.Match(prompt, @"Title:\s*(.+?)(?=\n|Description:)");
        if (titleMatch.Success)
        {
            title = titleMatch.Groups[1].Value.Trim();
        }
        
        var descMatch = System.Text.RegularExpressions.Regex.Match(prompt, @"Description:\s*(.+?)(?=\n|Existing Labels:|Status:)");
        if (descMatch.Success)
        {
            description = descMatch.Groups[1].Value.Trim();
        }
        
        var statusMatch = System.Text.RegularExpressions.Regex.Match(prompt, @"Status:\s*(.+?)(?=\n|$)");
        string status = statusMatch.Success ? statusMatch.Groups[1].Value.Trim().ToLower() : "";
        
        _logger.LogInformation("Extracted context - Title: {Title}, Status: {Status}, Description length: {DescLength}", 
            title, status, description?.Length ?? 0);
        
        // Generate contextual tags
        var contextualTags = new List<string>();
        
        // Add status as a tag
        if (!string.IsNullOrEmpty(status))
        {
            contextualTags.Add(status);
        }
        
        string contentToAnalyze = $"{title} {description}".ToLower();
        
        // Security-related keywords
        if (contentToAnalyze.Contains("security") || contentToAnalyze.Contains("vulnerability") || 
            contentToAnalyze.Contains("exploit") || contentToAnalyze.Contains("hack") || 
            contentToAnalyze.Contains("attack") || contentToAnalyze.Contains("auth") ||
            contentToAnalyze.Contains("secure") || contentToAnalyze.Contains("risk") ||
            contentToAnalyze.Contains("threat") || contentToAnalyze.Contains("protection"))
        {
            contextualTags.Add("security");
            contextualTags.Add("security-risk");
        }
        
        // Performance-related keywords
        if (contentToAnalyze.Contains("slow") || contentToAnalyze.Contains("performance") || 
            contentToAnalyze.Contains("speed") || contentToAnalyze.Contains("fast") || 
            contentToAnalyze.Contains("optimize") || contentToAnalyze.Contains("lag") ||
            contentToAnalyze.Contains("efficient") || contentToAnalyze.Contains("bottleneck") ||
            contentToAnalyze.Contains("memory leak") || contentToAnalyze.Contains("cpu usage"))
        {
            contextualTags.Add("performance");
            contextualTags.Add("optimization");
        }
        
        // Bug-related keywords
        if (contentToAnalyze.Contains("bug") || contentToAnalyze.Contains("issue") || 
            contentToAnalyze.Contains("problem") || contentToAnalyze.Contains("crash") || 
            contentToAnalyze.Contains("error") || contentToAnalyze.Contains("fix") || 
            contentToAnalyze.Contains("broken") || contentToAnalyze.Contains("exception") ||
            contentToAnalyze.Contains("fails") || contentToAnalyze.Contains("not working"))
        {
            contextualTags.Add("bug");
            
            // Add more specific bug categories
            if (contentToAnalyze.Contains("crash") || contentToAnalyze.Contains("exception"))
            {
                contextualTags.Add("crash");
            }
            
            if (contentToAnalyze.Contains("ui") || contentToAnalyze.Contains("display") || 
                contentToAnalyze.Contains("screen") || contentToAnalyze.Contains("visual"))
            {
                contextualTags.Add("ui-bug");
            }
        }
        
        // Feature-related keywords
        if (contentToAnalyze.Contains("feature") || contentToAnalyze.Contains("enhancement") || 
            contentToAnalyze.Contains("add") || contentToAnalyze.Contains("implement") || 
            contentToAnalyze.Contains("new") || contentToAnalyze.Contains("request") ||
            contentToAnalyze.Contains("support for") || contentToAnalyze.Contains("ability to"))
        {
            contextualTags.Add("enhancement");
            contextualTags.Add("feature-request");
        }
        
        // Documentation-related keywords
        if (contentToAnalyze.Contains("doc") || contentToAnalyze.Contains("documentation") || 
            contentToAnalyze.Contains("example") || contentToAnalyze.Contains("readme") || 
            contentToAnalyze.Contains("wiki") || contentToAnalyze.Contains("guide") ||
            contentToAnalyze.Contains("tutorial") || contentToAnalyze.Contains("manual"))
        {
            contextualTags.Add("documentation");
        }
        
        // VPN-specific tags 
        if (contentToAnalyze.Contains("vpn") || contentToAnalyze.Contains("connection") || 
            contentToAnalyze.Contains("network") || contentToAnalyze.Contains("connect") || 
            contentToAnalyze.Contains("tunnel") || contentToAnalyze.Contains("traffic") ||
            contentToAnalyze.Contains("protocol") || contentToAnalyze.Contains("proxy") ||
            contentToAnalyze.Contains("firewall") || contentToAnalyze.Contains("routing"))
        {
            contextualTags.Add("vpn");
            contextualTags.Add("networking");
            
            // Add more specific VPN tags
            if (contentToAnalyze.Contains("wireguard") || contentToAnalyze.Contains("wg"))
            {
                contextualTags.Add("wireguard");
            }
            
            if (contentToAnalyze.Contains("openvpn") || contentToAnalyze.Contains("ovpn"))
            {
                contextualTags.Add("openvpn");
            }
            
            if (contentToAnalyze.Contains("connect") || contentToAnalyze.Contains("connection") ||
                contentToAnalyze.Contains("disconnect") || contentToAnalyze.Contains("timeout"))
            {
                contextualTags.Add("connectivity");
            }
        }
        
        // Authentication-related keywords
        if (contentToAnalyze.Contains("login") || contentToAnalyze.Contains("auth") || 
            contentToAnalyze.Contains("sign in") || contentToAnalyze.Contains("password") || 
            contentToAnalyze.Contains("credential") || contentToAnalyze.Contains("oauth") ||
            contentToAnalyze.Contains("token") || contentToAnalyze.Contains("authenticate") ||
            contentToAnalyze.Contains("session") || contentToAnalyze.Contains("logout"))
        {
            contextualTags.Add("authentication");
            contextualTags.Add("user-account");
            
            if (contentToAnalyze.Contains("password") || contentToAnalyze.Contains("reset"))
            {
                contextualTags.Add("password-management");
            }
        }
        
        // Installation/setup keywords
        if (contentToAnalyze.Contains("install") || contentToAnalyze.Contains("setup") || 
            contentToAnalyze.Contains("configuration") || contentToAnalyze.Contains("setting") || 
            contentToAnalyze.Contains("deploy") || contentToAnalyze.Contains("initialize"))
        {
            contextualTags.Add("installation");
            contextualTags.Add("setup");
        }
        
        // Mobile-specific keywords
        if (contentToAnalyze.Contains("mobile") || contentToAnalyze.Contains("android") || 
            contentToAnalyze.Contains("ios") || contentToAnalyze.Contains("iphone") || 
            contentToAnalyze.Contains("app") || contentToAnalyze.Contains("smartphone"))
        {
            contextualTags.Add("mobile");
            
            if (contentToAnalyze.Contains("android"))
            {
                contextualTags.Add("android");
            }
            
            if (contentToAnalyze.Contains("ios") || contentToAnalyze.Contains("iphone") || contentToAnalyze.Contains("ipad"))
            {
                contextualTags.Add("ios");
            }
        }
        
        // Add priority tags
        bool hasPriority = false;
        if (contentToAnalyze.Contains("urgent") || contentToAnalyze.Contains("critical") || 
            contentToAnalyze.Contains("important") || contentToAnalyze.Contains("high priority"))
        {
            contextualTags.Add("high-priority");
            hasPriority = true;
        }
        else if (contentToAnalyze.Contains("low priority") || contentToAnalyze.Contains("minor"))
        {
            contextualTags.Add("low-priority");
            hasPriority = true;
        }
        
        // Make sure we have enough tags
        if (contextualTags.Count < 3)
        {
            // Add default tags based on status
            if (status == "closed")
            {
                contextualTags.Add("resolved");
            }
            else
            {
                contextualTags.Add("open");
                if (!hasPriority)
                {
                    contextualTags.Add("medium-priority");
                }
                contextualTags.Add("needs-triage");
            }
            
            // Add domain-specific tags based on repository name or title pattern
            if (prompt.Contains("ether-vpn"))
            {
                contextualTags.Add("vpn");
                contextualTags.Add("networking");
                contextualTags.Add("connectivity");
            }
            
            // Add some general tags from our pool
            var allTags = new[]
            {
                "ui", "accessibility", "api", "backend", "frontend", "mobile", 
                "testing", "infrastructure", "database", "analytics", "refactoring", 
                "tech-debt", "usability", "ux", "configuration", "logging",
                "error-handling", "validation", "release", "compatibility"
            };

            var additionalTags = allTags
                .OrderBy(_ => _random.Next())
                .Take(Math.Max(0, 5 - contextualTags.Count))
                .ToList();
                
            contextualTags.AddRange(additionalTags);
        }
        
        return string.Join(", ", contextualTags.Distinct());
    }

    private string GenerateRandomTags()
    {
        var allTags = new[]
        {
            "bug", "feature", "enhancement", "documentation", "performance", "security", 
            "ux", "ui", "accessibility", "api", "backend", "frontend", "mobile", 
            "testing", "ci/cd", "infrastructure", "database", "analytics", "refactoring", 
            "tech-debt", "high-priority", "low-priority", "medium-priority", "vpn",
            "networking", "authentication", "configuration", "deployment", "crash",
            "connectivity", "usability", "compatibility", "validation", "needs-triage"
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
            "Consolidate similar feature requests into coherent implementation plans",
            "Fix authentication and login issues which are causing users problems",
            "Update VPN connection handling to improve reliability",
            "Prioritize networking stability issues that impact core functionality",
            "Enhance user interface to improve overall user experience",
            "Address reported security vulnerabilities before adding new features",
            "Add better logging for troubleshooting recurring connectivity issues",
            "Implement automated regression testing for core features",
            "Create better onboarding documentation for new users",
            "Improve cross-platform compatibility based on user reports",
            "Add better error messaging for common installation issues"
        };

        var selectedRecommendations = allRecommendations
            .OrderBy(_ => _random.Next())
            .Take(_random.Next(3, 5))
            .ToArray();

        return string.Join("\n", selectedRecommendations);
    }
} 