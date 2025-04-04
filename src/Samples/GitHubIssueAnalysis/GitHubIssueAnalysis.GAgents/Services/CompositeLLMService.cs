using Microsoft.Extensions.Logging;

namespace GitHubIssueAnalysis.GAgents.Services;

/// <summary>
/// A composite LLM service that tries multiple providers in sequence until one succeeds
/// </summary>
public class CompositeLLMService : ILLMService
{
    private readonly ILogger<CompositeLLMService> _logger;
    private readonly IEnumerable<ILLMService> _services;
    private int _requestCount = 0;

    public CompositeLLMService(
        ILogger<CompositeLLMService> logger,
        IEnumerable<ILLMService> services)
    {
        _logger = logger;
        _services = services;
        
        _logger.LogWarning("CompositeLLMService constructed with {Count} services: [{Services}]", 
            services.Count(), 
            string.Join(", ", services.Select(s => s.GetType().Name)));
        
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"\n***** COMPOSITE LLM SERVICE INITIALIZED WITH {services.Count()} SERVICES *****");
        int i = 0;
        foreach (var service in services)
        {
            i++;
            Console.WriteLine($"  {i}. {service.GetType().Name}");
        }
        Console.WriteLine("*************************************************************\n");
        Console.ResetColor();
    }

    public async Task<string> CompletePromptAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) 
        {
            _logger.LogError("Prompt is empty or whitespace. Cannot process empty prompt.");
            return "ERROR: Empty prompt provided to LLM service.";
        }

        int requestId = Interlocked.Increment(ref _requestCount);
        
        _logger.LogWarning("[Request #{RequestId}] ===============================================================", requestId);
        _logger.LogWarning("[Request #{RequestId}] Starting LLM request with {Count} available services", requestId, _services.Count());
        _logger.LogWarning("[Request #{RequestId}] Prompt length: {Length} characters", requestId, prompt?.Length ?? 0);
        _logger.LogWarning("[Request #{RequestId}] Prompt preview: {Preview}", requestId, prompt?.Length > 100 ? prompt.Substring(0, 100) + "..." : prompt);
        _logger.LogWarning("[Request #{RequestId}] ===============================================================", requestId);
        
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"\n[Request #{requestId}] =====================================================");
        Console.WriteLine($"[Request #{requestId}] STARTING LLM REQUEST WITH {_services.Count()} SERVICES");
        Console.WriteLine($"[Request #{requestId}] PROMPT LENGTH: {prompt?.Length ?? 0} CHARACTERS");
        Console.WriteLine($"[Request #{requestId}] PROMPT PREVIEW: {(prompt?.Length > 50 ? prompt.Substring(0, 50) + "..." : prompt)}");
        Console.WriteLine($"[Request #{requestId}] =====================================================\n");
        Console.ResetColor();
        
        int serviceIndex = 0;
        var servicesList = _services.ToList();
        
        foreach (var service in servicesList)
        {
            serviceIndex++;
            bool isLastService = serviceIndex == servicesList.Count;
            bool isFallbackService = service is FallbackLLMService;
            string serviceName = service.GetType().Name;
            
            try
            {
                _logger.LogWarning("[Request #{RequestId}] Trying LLM service {Index}/{Total}: {ServiceType}", 
                    requestId, serviceIndex, servicesList.Count, serviceName);
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[Request #{requestId}] >>> TRYING LLM SERVICE {serviceIndex}/{servicesList.Count}: {serviceName}");
                Console.ResetColor();
                
                var serviceTask = service.CompletePromptAsync(prompt);
                var timeoutTask = Task.Delay(isFallbackService ? 45000 : 30000);
                
                var startTime = DateTime.UtcNow;
                
                var completedTask = await Task.WhenAny(serviceTask, timeoutTask);
                
                var elapsedTime = DateTime.UtcNow - startTime;
                _logger.LogWarning("[Request #{RequestId}] Service {ServiceType} took {ElapsedMs}ms to respond", 
                    requestId, serviceName, elapsedTime.TotalMilliseconds);
                
                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("[Request #{RequestId}] Service {ServiceType} timed out after {TimeoutMs}ms", 
                        requestId, serviceName, isFallbackService ? 45000 : 30000);
                    
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Request #{requestId}] >>> SERVICE {serviceName} TIMED OUT AFTER {(isFallbackService ? 45 : 30)} SECONDS");
                    Console.ResetColor();
                    
                    if (isLastService)
                    {
                        _logger.LogWarning("[Request #{RequestId}] This is the fallback service, continuing to wait for a response", requestId);
                        Console.WriteLine($"[Request #{requestId}] >>> CONTINUING TO WAIT FOR FALLBACK SERVICE RESPONSE...");
                        
                        try {
                            var result = await serviceTask;
                            
                            if (!string.IsNullOrWhiteSpace(result))
                            {
                                _logger.LogWarning("[Request #{RequestId}] Fallback service eventually responded successfully", requestId);
                                
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[Request #{requestId}] >>> FALLBACK SERVICE EVENTUALLY RESPONDED SUCCESSFULLY");
                                Console.ResetColor();
                                
                                return result;
                            }
                        }
                        catch (Exception ex) {
                            _logger.LogError(ex, "[Request #{RequestId}] Error while waiting for fallback service: {Error}", requestId, ex.Message);
                        }
                    }
                    
                    continue;
                }
                
                var response = await serviceTask;
                
                if (!string.IsNullOrWhiteSpace(response))
                {
                    _logger.LogWarning("[Request #{RequestId}] Successfully got response from {ServiceType} in {ElapsedMs}ms", 
                        requestId, serviceName, elapsedTime.TotalMilliseconds);
                    _logger.LogWarning("[Request #{RequestId}] Response length: {Length} characters", 
                        requestId, response.Length);
                    
                    string responsePreview = response.Length > 100 ? response.Substring(0, 100) + "..." : response;
                    _logger.LogInformation("[Request #{RequestId}] Response preview: {Preview}", requestId, responsePreview);
                    
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[Request #{requestId}] >>> SUCCESS! GOT RESPONSE FROM {serviceName} IN {elapsedTime.TotalMilliseconds:F0}ms");
                    Console.WriteLine($"[Request #{requestId}] >>> RESPONSE LENGTH: {response.Length} CHARACTERS");
                    Console.WriteLine($"[Request #{requestId}] >>> PREVIEW: {(response.Length > 50 ? response.Substring(0, 50) + "..." : response)}");
                    Console.ResetColor();
                    
                    return response;
                }
                
                _logger.LogWarning("[Request #{RequestId}] Service {ServiceType} returned empty result, trying next service", 
                    requestId, serviceName);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Request #{requestId}] >>> SERVICE {serviceName} RETURNED EMPTY RESULT, TRYING NEXT");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Request #{RequestId}] Error using LLM service {ServiceType}: {Error}", 
                    requestId, serviceName, ex.Message);
                
                if (isFallbackService || isLastService)
                {
                    _logger.LogError(ex, "[Request #{RequestId}] CRITICAL ERROR: Fallback LLM service failed with exception: {ExMessage}", 
                        requestId, ex.Message);
                    
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Request #{requestId}] >>> CRITICAL ERROR: FALLBACK SERVICE FAILED WITH: {ex.Message}");
                    Console.WriteLine($"[Request #{requestId}] >>> ATTEMPTING TO PROVIDE MINIMAL RESPONSE");
                    Console.ResetColor();
                    
                    if (prompt.Contains("tags") || prompt.Contains("Tags"))
                    {
                        _logger.LogWarning("[Request #{RequestId}] Providing basic tag response", requestId);
                        return "bug, enhancement, needs-review, documentation, open";
                    }
                    else if (prompt.Contains("recommendation") || prompt.Contains("Recommendation"))
                    {
                        _logger.LogWarning("[Request #{RequestId}] Providing basic recommendation response", requestId);
                        return "RECOMMENDATION 1:\nTitle: Review Repository Issues\nPriority: Medium\nDescription: Conduct a thorough review of open issues to identify and prioritize critical fixes.\nSupporting Issues: General improvement\n";
                    }
                    else
                    {
                        _logger.LogWarning("[Request #{RequestId}] Providing generic response", requestId);
                        return "Unable to process request. Please try again later.";
                    }
                }
            }
        }

        _logger.LogError("[Request #{RequestId}] CRITICAL ERROR: All LLM services failed", requestId);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Request #{requestId}] >>> CRITICAL ERROR: ALL LLM SERVICES FAILED");
        Console.ResetColor();
        
        return "ERROR: All available LLM services failed to process your request. Please try again later.";
    }
} 