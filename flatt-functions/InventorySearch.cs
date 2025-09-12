using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using flatt_functions.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic; // fix: needed for List<> types
using System; // for Uri unescape

namespace flatt_functions
{
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
        private readonly InventoryContext _context;
        private readonly ILogger<InventorySearch> _logger;

        public InventorySearch(InventoryContext context, ILogger<InventorySearch> logger)
        {
            _context = context;
            _logger = logger;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            var trimmed = query.StartsWith("?") ? query.Substring(1) : query;
            foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0] ?? "");
                var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                dict[key] = value;
            }
            return dict;
        }

        [Function("InventorySearch")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
        {
            var query = ParseQuery(req.Url.Query);
            var q = query.TryGetValue("q", out var qVal) ? (qVal ?? string.Empty).Trim() : string.Empty;

            int? typeIdFilter = null;
            if (query.TryGetValue("typeId", out var typeStr) && int.TryParse(typeStr, out var parsedTypeId))
                typeIdFilter = parsedTypeId;

            _logger.LogInformation("InventorySearch started. q='{q}', typeId={typeId}", q, typeIdFilter);

            var baseQuery = _context.Units
                .Include(u => u.UnitType)
                .Include(u => u.UnitFeatures)
                .Include(u => u.UnitImages)
                .AsNoTracking();

            if (typeIdFilter.HasValue)
            {
                baseQuery = baseQuery.Where(u => u.TypeID == typeIdFilter.Value);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                baseQuery = baseQuery.Where(u =>
                    (u.Name ?? "").Contains(q) ||
                    (u.Model ?? "").Contains(q) ||
                    (u.Description ?? "").Contains(q));
            }

            var units = await baseQuery
                .OrderBy(u => u.Name)
                .ToListAsync();

            var results = units.Select(unit => new InventoryItem
            {
                Id = unit.UnitID.ToString(),
                Model = unit.Model ?? string.Empty,
                Year = unit.Year ?? 0,
                Price = unit.Price ?? 0m,
                Status = unit.Status ?? string.Empty,
                ImageUrl = unit.UnitImages?
                    .OrderBy(img => img.DisplayOrder)
                    .Select(img => img.ImageURL ?? string.Empty)
                    .FirstOrDefault() ?? string.Empty,
                Description = unit.Description ?? string.Empty,
                Features = unit.UnitFeatures?
                    .Select(f => f.FeatureName ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList() ?? new List<string>()
            }).ToList();

            var response = new InventoryResponse
            {
                Success = true,
                Data = results,
                Message = "Search completed",
                TotalCount = results.Count
            };

            var httpResponse = req.CreateResponse(HttpStatusCode.OK);
            httpResponse.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await httpResponse.WriteStringAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return httpResponse;
        }
    }
}
