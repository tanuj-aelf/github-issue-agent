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

        // Configure Azure OpenAI Options from configuration or environment variables
        if (configuration != null)
        {
            services.Configure<AzureOpenAIOptions>(options =>
            {
                // First try configuration
                options.ApiKey = configuration["AzureOpenAI:ApiKey"] ?? "";
                options.Endpoint = configuration["AzureOpenAI:Endpoint"] ?? "";
                options.DeploymentName = configuration["AzureOpenAI:DeploymentName"] ?? "";
                options.ModelName = configuration["AzureOpenAI:ModelName"] ?? "gpt-35-turbo";
                options.ApiVersion = configuration["AzureOpenAI:ApiVersion"] ?? "2024-02-15-preview";
                
                // If values are empty, try environment variables
                if (string.IsNullOrEmpty(options.ApiKey))
                    options.ApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "";
                
                if (string.IsNullOrEmpty(options.Endpoint))
                    options.Endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "";
                
                if (string.IsNullOrEmpty(options.DeploymentName))
                    options.DeploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "";
                
                if (string.IsNullOrEmpty(options.ModelName))
                    options.ModelName = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_NAME") ?? "gpt-35-turbo";
                
                if (string.IsNullOrEmpty(options.ApiVersion))
                    options.ApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-02-15-preview";
            });
        }
        else 
        {
            // If no configuration, try environment variables
            services.Configure<AzureOpenAIOptions>(options =>
            {
                options.ApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "";
                options.Endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "";
                options.DeploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "";
                options.ModelName = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_NAME") ?? "gpt-35-turbo";
                options.ApiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-02-15-preview";
            });
        }

        // Add HTTP client for Azure OpenAI with Polly for resilience
        services.AddHttpClient("AzureOpenAI")
            .AddPolicyHandler(GetRetryPolicy())
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // Configure Google Gemini Options from configuration or environment variables
        if (configuration != null)
        {
            services.Configure<GoogleGeminiOptions>(options =>
            {
                // First try configuration
                options.ApiKey = configuration["GoogleGemini:ApiKey"] ?? "";
                options.Model = configuration["GoogleGemini:Model"] ?? "gemini-pro";
                
                // If values are empty, try environment variables
                if (string.IsNullOrEmpty(options.ApiKey))
                    options.ApiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY") ?? "";
                
                if (string.IsNullOrEmpty(options.Model))
                    options.Model = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL") ?? "gemini-pro";
            });
        }
        else
        {
            // If no configuration, try environment variables
            services.Configure<GoogleGeminiOptions>(options =>
            {
                options.ApiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY") ?? "";
                options.Model = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL") ?? "gemini-pro";
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
            var azureApiKey = configuration?["AzureOpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "";
            var azureEndpoint = configuration?["AzureOpenAI:Endpoint"] ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "";
            var geminiApiKey = configuration?["GoogleGemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY") ?? "";
            
            // Try configuration first, then environment variable
            bool useFallbackLLM = configuration?.GetValue<bool>("UseFallbackLLM") ?? false;
            if (!useFallbackLLM)
            {
                string envFallback = Environment.GetEnvironmentVariable("USE_FALLBACK_LLM") ?? "";
                useFallbackLLM = !string.IsNullOrEmpty(envFallback) && 
                                 (envFallback.ToLower() == "true" || envFallback == "1");
            }
            
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
            if (!string.IsNullOrEmpty(azureApiKey))
            {
                logger.LogWarning("Adding AzureOpenAIService to composite service");
                services.Add(provider.GetRequiredService<AzureOpenAIService>());
            }
            
            if (!string.IsNullOrEmpty(geminiApiKey))
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