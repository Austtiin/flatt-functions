using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace flatt_functions;

public class InventoryItem
{
    public string Id { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Features { get; set; } = new List<string>();
}

public class InventoryResponse
{
    public bool Success { get; set; }
    public List<InventoryItem> Data { get; set; } = new List<InventoryItem>();
    public string Message { get; set; } = string.Empty;
    public int TotalCount { get; set; }
}

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
            
            // Create sample inventory data
            var inventoryData = new List<InventoryItem>
            {
                new InventoryItem
                {
                    Id = "ICE001",
                    Model = "8x21 RV Edition",
                    Year = 2024,
                    Price = 89999,
                    Status = "available",
                    ImageUrl = "https://example.com/images/ice001.jpg",
                    Description = "Premium ice house with full RV amenities",
                    Features = new List<string> { "Full Kitchen", "Bathroom", "Sleeping Area", "Fish House" }
                },
                new InventoryItem
                {
                    Id = "ICE002",
                    Model = "6x17 Fish House",
                    Year = 2023,
                    Price = 65999,
                    Status = "sold",
                    ImageUrl = "https://example.com/images/ice002.jpg",
                    Description = "Compact fishing house perfect for weekend trips",
                    Features = new List<string> { "Fold Down Bunks", "Dinette", "Basic Kitchen" }
                }
            };

            // Create the response object
            var response = new InventoryResponse
            {
                Success = true,
                Data = inventoryData,
                Message = "Inventory fetched successfully",
                TotalCount = inventoryData.Count
            };

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request");
            
            var errorResponse = new InventoryResponse
            {
                Success = false,
                Data = new List<InventoryItem>(),
                Message = $"An error occurred processing the request: {ex.Message}",
                TotalCount = 0
            };
            
            return new BadRequestObjectResult(errorResponse);
        }
    }
}
