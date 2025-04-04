using GitHubIssueAnalysis.GAgents.GitHubAnalysis;
using GitHubIssueAnalysis.GAgents.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using Polly;
using Polly.Extensions.Http;
using Octokit;
using Microsoft.Extensions.Logging;

namespace GitHubIssueAnalysis.GAgents;

public static class GitHubIssueAnalysisGAgentsModule
{
    public static IServiceCollection AddGitHubIssueAnalysisGAgents(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Register GAgents
        services.AddTransient<IGitHubAnalysisGAgent, GitHubAnalysisGAgent>();

        // Register GitHub client
        services.AddTransient<GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient>(provider => 
        {
            // Use a Personal Access Token (PAT) from configuration if available
            // First check appsettings.json, then environment variables
            string personalAccessToken = configuration?.GetValue<string>("GitHub:PersonalAccessToken") ?? "";
            
            // If token is empty, try environment variable
            if (string.IsNullOrEmpty(personalAccessToken))
            {
                personalAccessToken = Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN") ?? "";
                var logger = provider.GetRequiredService<ILogger<GitHubAnalysisGAgent>>();
                logger.LogInformation("Using GitHub token from environment variable");
            }
            
            return new GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient(personalAccessToken);
        });

        // Configure Google Gemini Options from configuration or environment variables
        services.Configure<GoogleGeminiOptions>(options =>
        {
            // Get API key from environment variable
            string apiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY") ?? "";
            if (string.IsNullOrEmpty(apiKey)) 
            {
                apiKey = configuration?.GetValue<string>("GoogleGemini:ApiKey") ?? "";
            }
            options.ApiKey = apiKey;
            
            // Get model from environment variable
            string model = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL") ?? "";
            if (string.IsNullOrEmpty(model))
            {
                model = configuration?.GetValue<string>("GoogleGemini:Model") ?? "gemini-1.5-flash";
            }
            options.Model = model;
            
            // Log configuration
            var loggerFactory = services.BuildServiceProvider().GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("GoogleGeminiConfig");
            logger?.LogInformation("Configured Gemini with API key length: {Length}, Model: {Model}", 
                apiKey?.Length ?? 0, model);
        });

        // Add HTTP client for Google Gemini with Polly for resilience
        services.AddHttpClient("GoogleGemini")
            .AddPolicyHandler(GetRetryPolicy())
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // Register all LLM service implementations
        services.AddTransient<GoogleGeminiService>();
        services.AddTransient<FallbackLLMService>();

        // Register composite LLM service with Gemini prioritized
        services.AddTransient<CompositeLLMService>(provider => 
        {
            var logger = provider.GetRequiredService<ILogger<CompositeLLMService>>();
            var geminiService = provider.GetRequiredService<GoogleGeminiService>();
            var fallbackService = provider.GetRequiredService<FallbackLLMService>();
            
            // Get API key for logging
            var geminiApiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY") ?? "";
            var geminiModel = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL") ?? "gemini-1.5-flash";
            
            // Log configuration
            logger.LogWarning("Configuring CompositeLLMService prioritizing Google Gemini API");
            logger.LogWarning("Gemini API key present: {HasKey}", !string.IsNullOrEmpty(geminiApiKey));
            logger.LogWarning("Gemini model: {Model}", geminiModel);
            
            Console.WriteLine("\n***************** LLM CONFIGURATION *****************");
            Console.WriteLine($"Google Gemini API Key: {(string.IsNullOrEmpty(geminiApiKey) ? "Not found" : $"Present [{geminiApiKey.Length} chars]")}");
            Console.WriteLine($"Google Gemini Model: {geminiModel}");
            Console.WriteLine("****************************************************\n");
            
            // Create the service list
            var services = new List<ILLMService>();
            
            // Always try Gemini first if we have an API key
            if (!string.IsNullOrEmpty(geminiApiKey))
            {
                logger.LogWarning("Adding GoogleGeminiService as primary LLM service");
                Console.WriteLine("USING GOOGLE GEMINI AS PRIMARY LLM SERVICE");
                services.Add(geminiService);
            }
            
            // Always add fallback as last resort
            logger.LogWarning("Adding FallbackLLMService as fallback");
            Console.WriteLine("ADDING FALLBACK LLM SERVICE AS FALLBACK");
            services.Add(fallbackService);
            
            return new CompositeLLMService(logger, services);
        });

        // Use the composite service as the main LLM service
        services.AddTransient<ILLMService>(provider => provider.GetRequiredService<CompositeLLMService>());

        return services;
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
    }
} 