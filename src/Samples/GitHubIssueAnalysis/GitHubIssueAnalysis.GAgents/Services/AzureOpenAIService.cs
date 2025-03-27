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
            _logger.LogWarning("===== AZURE OPENAI API CALL STARTING =====");
            
            _logger.LogWarning("Endpoint: {Endpoint}", _options.Endpoint);
            _logger.LogWarning("DeploymentName: {DeploymentName}", _options.DeploymentName);
            _logger.LogWarning("ModelName: {ModelName}", _options.ModelName);
            _logger.LogWarning("ApiVersion: {ApiVersion}", _options.ApiVersion);
            _logger.LogWarning("ApiKey present: {HasApiKey}", !string.IsNullOrEmpty(_options.ApiKey));
            
            if (string.IsNullOrEmpty(_options.ApiKey))
            {
                _logger.LogError("Azure OpenAI API key is empty! Cannot make API call.");
                return string.Empty;
            }
            
            if (string.IsNullOrEmpty(_options.Endpoint))
            {
                _logger.LogError("Azure OpenAI endpoint is empty! Cannot make API call.");
                return string.Empty;
            }
            
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
            
            // Clear any existing headers to avoid duplicates
            if (_httpClient.DefaultRequestHeaders.Contains("api-key"))
            {
                _httpClient.DefaultRequestHeaders.Remove("api-key");
            }
            
            // Add API key to headers
            _httpClient.DefaultRequestHeaders.Add("api-key", _options.ApiKey);
            
            _logger.LogWarning("Sending API request to: {Endpoint}", endpoint);
            var response = await _httpClient.PostAsync(endpoint, content);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Azure OpenAI API returned error: {StatusCode}, Response: {Response}", 
                    response.StatusCode, responseBody);
                return string.Empty;
            }
            
            _logger.LogWarning("Successfully received response from Azure OpenAI API");
            
            try
            {
                var responseJson = JsonDocument.Parse(responseBody);
                
                // Extract the content from the response
                var contentElement = responseJson
                    .RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content");
                    
                var result = contentElement.GetString() ?? string.Empty;
                _logger.LogWarning("Successfully parsed OpenAI response");
                return result;
            }
            catch (Exception jsonEx)
            {
                _logger.LogError(jsonEx, "Error parsing API response: {Response}", responseBody);
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure OpenAI API: {Message}", ex.Message);
            return string.Empty;
        }
        finally
        {
            _logger.LogWarning("===== AZURE OPENAI API CALL COMPLETED =====");
        }
    }
} 