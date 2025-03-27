using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitHubIssueAnalysis.GAgents.Services;

public class AzureOpenAIService : ILLMService
{
    private readonly ILogger<AzureOpenAIService> _logger;
    private readonly HttpClient _httpClient;
    private readonly AzureOpenAIOptions _options;

    public AzureOpenAIService(
        ILogger<AzureOpenAIService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<AzureOpenAIOptions> options)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("AzureOpenAI");
        _options = options.Value;
    }

    public async Task<string> CompletePromptAsync(string prompt)
    {
        try
        {
            var endpoint = $"{_options.Endpoint}/openai/deployments/{_options.DeploymentName}/chat/completions?api-version={_options.ApiVersion}";
            
            var requestBody = new
            {
                model = _options.ModelName,
                temperature = 0.3,
                messages = new[]
                {
                    new { role = "system", content = "You are a helpful AI assistant specialized in analyzing GitHub issues." },
                    new { role = "user", content = prompt }
                }
            };

            var requestJson = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            
            // Add API key to headers
            _httpClient.DefaultRequestHeaders.Add("api-key", _options.ApiKey);
            
            var response = await _httpClient.PostAsync(endpoint, content);
            response.EnsureSuccessStatusCode();
            
            var responseBody = await response.Content.ReadAsStringAsync();
            var responseJson = JsonDocument.Parse(responseBody);
            
            // Extract the content from the response
            var contentElement = responseJson
                .RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content");
                
            return contentElement.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure OpenAI API");
            return string.Empty;
        }
    }
} 