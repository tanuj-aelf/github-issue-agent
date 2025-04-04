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
        services.AddTransient<IGitHubAnalysisGAgent, GitHubAnalysisGAgent>();

        services.AddTransient<GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient>(provider => 
        {
            string personalAccessToken = configuration?.GetValue<string>("GitHub:PersonalAccessToken") ?? "";
            
            if (string.IsNullOrEmpty(personalAccessToken))
            {
                personalAccessToken = Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN") ?? "";
                var logger = provider.GetRequiredService<ILogger<GitHubAnalysisGAgent>>();
                logger.LogInformation("Using GitHub token from environment variable");
            }
            
            return new GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient(personalAccessToken);
        });

        services.Configure<GoogleGeminiOptions>(options =>
        {
            string apiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY") ?? "";
            if (string.IsNullOrEmpty(apiKey)) 
            {
                apiKey = configuration?.GetValue<string>("GoogleGemini:ApiKey") ?? "";
            }
            options.ApiKey = apiKey;
            
            string model = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL") ?? "";
            if (string.IsNullOrEmpty(model))
            {
                model = configuration?.GetValue<string>("GoogleGemini:Model") ?? "gemini-1.5-flash";
            }
            options.Model = model;
            
            var loggerFactory = services.BuildServiceProvider().GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("GoogleGeminiConfig");
            logger?.LogInformation("Configured Gemini with API key length: {Length}, Model: {Model}", 
                apiKey?.Length ?? 0, model);
        });

        services.AddHttpClient("GoogleGemini")
            .AddPolicyHandler(GetRetryPolicy())
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        services.AddTransient<GoogleGeminiService>();
        services.AddTransient<FallbackLLMService>();

        services.AddTransient<CompositeLLMService>(provider => 
        {
            var logger = provider.GetRequiredService<ILogger<CompositeLLMService>>();
            var geminiService = provider.GetRequiredService<GoogleGeminiService>();
            var fallbackService = provider.GetRequiredService<FallbackLLMService>();
            
            var geminiApiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY") ?? "";
            var geminiModel = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL") ?? "gemini-1.5-flash";
            
            logger.LogWarning("Configuring CompositeLLMService prioritizing Google Gemini API");
            logger.LogWarning("Gemini API key present: {HasKey}", !string.IsNullOrEmpty(geminiApiKey));
            logger.LogWarning("Gemini model: {Model}", geminiModel);
            
            Console.WriteLine("\n***************** LLM CONFIGURATION *****************");
            Console.WriteLine($"Google Gemini API Key: {(string.IsNullOrEmpty(geminiApiKey) ? "Not found" : $"Present [{geminiApiKey.Length} chars]")}");
            Console.WriteLine($"Google Gemini Model: {geminiModel}");
            Console.WriteLine("****************************************************\n");
            
            var services = new List<ILLMService>();
            
            if (!string.IsNullOrEmpty(geminiApiKey))
            {
                logger.LogWarning("Adding GoogleGeminiService as primary LLM service");
                Console.WriteLine("USING GOOGLE GEMINI AS PRIMARY LLM SERVICE");
                services.Add(geminiService);
            }
            
            logger.LogWarning("Adding FallbackLLMService as fallback");
            Console.WriteLine("ADDING FALLBACK LLM SERVICE AS FALLBACK");
            services.Add(fallbackService);
            
            return new CompositeLLMService(logger, services);
        });

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