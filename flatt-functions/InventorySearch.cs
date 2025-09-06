using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace flatt_functions;

public class InventorySearch
{
    private readonly ILogger<InventorySearch> _logger;

    public InventorySearch(ILogger<InventorySearch> logger)
    {
        _logger = logger;
    }

    [Function("InventorySearch")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        return new OkObjectResult("Welcome to Azure Functions!!!");
    }
}
