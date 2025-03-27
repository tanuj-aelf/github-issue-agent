using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitHubIssueAnalysis.GAgents.Services;

public class GoogleGeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-pro";
}

public class GoogleGeminiService : ILLMService
{
    private readonly ILogger<GoogleGeminiService> _logger;
    private readonly HttpClient _httpClient;
    private readonly GoogleGeminiOptions _options;

    public GoogleGeminiService(
        ILogger<GoogleGeminiService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<GoogleGeminiOptions> options)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("GoogleGemini");
        _options = options.Value;
    }

    public async Task<string> CompletePromptAsync(string prompt)
    {
        try
        {
            _logger.LogInformation("Starting Google Gemini API call with prompt length: {Length}", prompt.Length);
            _logger.LogInformation("Model: {Model}, API Key: {KeyLength} chars", 
                _options.Model, 
                _options.ApiKey?.Length ?? 0);
            
            var endpoint = $"https://generativelanguage.googleapis.com/v1/models/{_options.Model}:generateContent?key={_options.ApiKey}";
            
            var requestBody = new
            {
                contents = new[]
                {
                    new 
                    { 
                        role = "user",
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.4,
                    maxOutputTokens = 800
                }
            };

            var requestJson = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            
            _logger.LogDebug("Sending API request to Google Gemini");
            var response = await _httpClient.PostAsync(endpoint, content);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Google Gemini API returned error: {StatusCode}, Response: {Response}", 
                    response.StatusCode, responseBody);
                return string.Empty;
            }
            
            _logger.LogInformation("Successfully received response from Google Gemini API");
            
            try
            {
                var responseJson = JsonDocument.Parse(responseBody);
                
                // Extract the content from the response
                var text = responseJson
                    .RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;
                    
                _logger.LogDebug("Parsed Gemini response: {Response}", text);
                return text;
            }
            catch (Exception jsonEx)
            {
                _logger.LogError(jsonEx, "Error parsing Google Gemini API response: {Response}", responseBody);
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Google Gemini API: {Message}", ex.Message);
            return string.Empty;
        }
    }
} 