using GitHubIssueAnalysis.GAgents.GitHubAnalysis;
using GitHubIssueAnalysis.GAgents.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using Polly;
using Polly.Extensions.Http;
using Octokit;

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

        // Register LLM Service - use AzureOpenAIService if API key is provided, otherwise use FallbackLLMService
        services.AddTransient<ILLMService>(provider =>
        {
            var apiKey = configuration?["AzureOpenAI:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
            {
                return provider.GetRequiredService<AzureOpenAIService>();
            }
            return provider.GetRequiredService<FallbackLLMService>();
        });

        // Register both implementations
        services.AddTransient<AzureOpenAIService>();
        services.AddTransient<FallbackLLMService>();

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