using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace flatt_functions;

public class InventorySearch
{
    private readonly ILogger<InventorySearch> _logger;

    public InventorySearch(ILogger<InventorySearch> logger)
    {
        _logger = logger;
    }

    [Function("InventorySearch")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
    {
        try
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            
            // Log the request details for debugging
            _logger.LogInformation($"Request method: {req.Method}");
            _logger.LogInformation($"Request path: {req.Path}");
            _logger.LogInformation($"Request query: {req.QueryString}");
            
            // Create a proper JSON response
            var response = new
            {
                message = "Welcome to Azure Functions!",
                timestamp = DateTime.UtcNow,
                method = req.Method,
                success = true
            };

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request");
            
            var errorResponse = new
            {
                error = "An error occurred processing the request",
                message = ex.Message,
                timestamp = DateTime.UtcNow,
                success = false
            };
            
            return new BadRequestObjectResult(errorResponse);
        }
    }
}
