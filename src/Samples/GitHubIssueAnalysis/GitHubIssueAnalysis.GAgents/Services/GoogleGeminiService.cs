using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitHubIssueAnalysis.GAgents.Services;

public class GoogleGeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.0-flash";
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
        
        // Override model from environment variable if available
        string envModel = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL");
        if (!string.IsNullOrEmpty(envModel))
        {
            _options.Model = envModel;
            _logger.LogInformation("Using Gemini model from environment: {Model}", _options.Model);
        }
        
        // Override API key from environment variable if available
        string envApiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(envApiKey) && string.IsNullOrEmpty(_options.ApiKey))
        {
            _options.ApiKey = envApiKey;
            _logger.LogInformation("Using Gemini API key from environment variable, length: {Length} chars", 
                _options.ApiKey.Length);
        }
    }

    public async Task<string> CompletePromptAsync(string prompt)
    {
        try
        {
            // Add console feedback for visibility
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n=================================================================");
            Console.WriteLine("**** GOOGLE GEMINI API CALL STARTING ****");
            Console.WriteLine($"**** MODEL: {_options.Model} ****");
            Console.WriteLine("=================================================================\n");
            Console.ResetColor();
            
            _logger.LogInformation("Starting Google Gemini API call with prompt length: {Length}", prompt.Length);
            _logger.LogInformation("Model: {Model}, API Key present: {HasKey}", 
                _options.Model, 
                !string.IsNullOrEmpty(_options.ApiKey));
            
            // Validate API key
            if (string.IsNullOrEmpty(_options.ApiKey))
            {
                _logger.LogError("Google Gemini API key is empty! Cannot make API call.");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: Google Gemini API key is empty. Make sure it's set in the .env file.");
                Console.ResetColor();
                return string.Empty;
            }
            
            // Log the first few characters of the API key for debugging
            _logger.LogDebug("API Key starts with: {KeyStart}", 
                _options.ApiKey.Length > 5 ? _options.ApiKey.Substring(0, 5) + "..." : "[too short]");
            
            var endpoint = $"https://generativelanguage.googleapis.com/v1/models/{_options.Model}:generateContent?key={_options.ApiKey}";
            
            _logger.LogDebug("Using endpoint: {Endpoint}", 
                endpoint.Replace(_options.ApiKey, "[REDACTED]"));
            
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

            Console.WriteLine($"Making request to Gemini API using model: {_options.Model}");
            Console.WriteLine($"Prompt length: {prompt.Length} characters");
            Console.WriteLine($"Request time: {DateTime.Now:HH:mm:ss.fff}");
            Console.WriteLine("Waiting for response from Google Gemini...");
            
            var requestJson = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            
            _logger.LogDebug("Sending API request to Google Gemini");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await _httpClient.PostAsync(endpoint, content);
            stopwatch.Stop();
            
            _logger.LogInformation("Received response from Google Gemini API in {ElapsedMs}ms with status: {StatusCode}", 
                stopwatch.ElapsedMilliseconds, response.StatusCode);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            
            // Always log response body for debugging
            _logger.LogDebug("Full response body: {ResponseBody}", responseBody);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Google Gemini API returned error: {StatusCode}, Response: {Response}", 
                    response.StatusCode, responseBody);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: Google Gemini API returned status code {response.StatusCode}");
                Console.WriteLine($"Response: {responseBody}");
                
                // Try to parse the error message
                try {
                    var errorDoc = JsonDocument.Parse(responseBody);
                    var errorMessage = errorDoc.RootElement.GetProperty("error").GetProperty("message").GetString();
                    Console.WriteLine($"Error message: {errorMessage}");
                    
                    // Check if it's a model-related error
                    if (errorMessage?.Contains("model") == true || errorMessage?.Contains("not found") == true) {
                        Console.WriteLine("\nPOSSIBLE MODEL ERROR: The model name may be incorrect.");
                        Console.WriteLine("Valid models include: gemini-1.5-flash, gemini-1.5-pro, gemini-pro");
                    }
                } catch {
                    // Ignore parsing errors
                }
                
                Console.ResetColor();
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
                    
                _logger.LogDebug("Parsed Gemini response: {Response}", 
                    text.Length > 100 ? text.Substring(0, 100) + "..." : text);
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n=================================================================");
                Console.WriteLine("**** SUCCESSFULLY RECEIVED RESPONSE FROM GOOGLE GEMINI ****");
                Console.WriteLine($"Response length: {text.Length} characters");
                Console.WriteLine($"Response time: {DateTime.Now:HH:mm:ss.fff}");
                Console.WriteLine("=================================================================\n");
                Console.ResetColor();
                
                // Log a snippet of the response
                Console.WriteLine("Response snippet:");
                Console.WriteLine(text.Length > 200 ? text.Substring(0, 200) + "..." : text);
                
                return text;
            }
            catch (Exception jsonEx)
            {
                _logger.LogError(jsonEx, "Error parsing Google Gemini API response: {Response}", responseBody);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR parsing Google Gemini response: {jsonEx.Message}");
                Console.WriteLine($"Raw response: {responseBody}");
                Console.ResetColor();
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Google Gemini API: {Message}", ex.Message);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR calling Google Gemini API: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Console.ResetColor();
            return string.Empty;
        }
    }
} 