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
            // For security reasons, you should use user secrets (dotnet user-secrets) or environment variables
            // This is just for demo purposes - in a real app, never store tokens in code
            string personalAccessToken = configuration?.GetValue<string>("GitHub:PersonalAccessToken") ?? "";
            
            return new GitHubIssueAnalysis.GAgents.GitHubAnalysis.GitHubClient(personalAccessToken);
        });

        // Configure Azure OpenAI Options if configuration is provided
        if (configuration != null)
        {
            services.Configure<AzureOpenAIOptions>(options =>
            {
                options.ApiKey = configuration["AzureOpenAI:ApiKey"] ?? "";
                options.Endpoint = configuration["AzureOpenAI:Endpoint"] ?? "";
                options.DeploymentName = configuration["AzureOpenAI:DeploymentName"] ?? "";
                options.ModelName = configuration["AzureOpenAI:ModelName"] ?? "gpt-35-turbo";
                options.ApiVersion = configuration["AzureOpenAI:ApiVersion"] ?? "2024-02-15-preview";
            });
        }
        else 
        {
            // Use default empty values if no configuration is provided
            services.Configure<AzureOpenAIOptions>(options =>
            {
                options.ApiKey = "";
                options.Endpoint = "";
                options.DeploymentName = "";
                options.ModelName = "gpt-35-turbo";
                options.ApiVersion = "2024-02-15-preview";
            });
        }

        // Add HTTP client for Azure OpenAI with Polly for resilience
        services.AddHttpClient("AzureOpenAI")
            .AddPolicyHandler(GetRetryPolicy())
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // Configure Google Gemini Options if configuration is provided
        if (configuration != null)
        {
            services.Configure<GoogleGeminiOptions>(options =>
            {
                options.ApiKey = configuration["GoogleGemini:ApiKey"] ?? "";
                options.Model = configuration["GoogleGemini:Model"] ?? "gemini-pro";
            });
        }
        else
        {
            services.Configure<GoogleGeminiOptions>(options =>
            {
                options.ApiKey = "";
                options.Model = "gemini-pro";
            });
        }

        // Add HTTP client for Google Gemini with Polly for resilience
        services.AddHttpClient("GoogleGemini")
            .AddPolicyHandler(GetRetryPolicy())
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // Register Google Gemini Service
        services.AddTransient<GoogleGeminiService>();

        // Register all LLM service implementations
        services.AddTransient<AzureOpenAIService>();
        services.AddTransient<GoogleGeminiService>();
        services.AddTransient<FallbackLLMService>();

        // Register composite LLM service that will try services in order
        services.AddTransient<CompositeLLMService>(provider => 
        {
            var logger = provider.GetRequiredService<ILogger<CompositeLLMService>>();
            logger.LogWarning("Creating CompositeLLMService");
            
            // Extract configuration settings for debugging
            var azureApiKey = configuration?["AzureOpenAI:ApiKey"] ?? "";
            var azureEndpoint = configuration?["AzureOpenAI:Endpoint"] ?? "";
            var geminiApiKey = configuration?["GoogleGemini:ApiKey"] ?? "";
            var useFallbackLLM = configuration?.GetValue<bool>("UseFallbackLLM") ?? false;
            
            logger.LogWarning("Azure OpenAI API Key present: {HasKey}", !string.IsNullOrEmpty(azureApiKey));
            logger.LogWarning("Azure OpenAI Endpoint present: {HasEndpoint}", !string.IsNullOrEmpty(azureEndpoint));
            logger.LogWarning("Google Gemini API Key present: {HasKey}", !string.IsNullOrEmpty(geminiApiKey));
            logger.LogWarning("UseFallbackLLM flag: {UseFallbackLLM}", useFallbackLLM);
            
            // Build the list of services to try in order
            var services = new List<ILLMService>();
            
            // If fallback flag is set, only use the mock service
            if (useFallbackLLM)
            {
                logger.LogWarning("UseFallbackLLM flag is set, using only the FallbackLLMService");
                services.Add(provider.GetRequiredService<FallbackLLMService>());
                return new CompositeLLMService(logger, services);
            }
            
            // Otherwise, try cloud services first (in order of preference)
            if (!string.IsNullOrEmpty(configuration?["AzureOpenAI:ApiKey"]))
            {
                logger.LogWarning("Adding AzureOpenAIService to composite service");
                services.Add(provider.GetRequiredService<AzureOpenAIService>());
            }
            
            if (!string.IsNullOrEmpty(configuration?["GoogleGemini:ApiKey"]))
            {
                logger.LogWarning("Adding GoogleGeminiService to composite service");
                services.Add(provider.GetRequiredService<GoogleGeminiService>());
            }
            
            // Always add fallback as last resort
            logger.LogWarning("Adding FallbackLLMService to composite service");
            services.Add(provider.GetRequiredService<FallbackLLMService>());
            
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