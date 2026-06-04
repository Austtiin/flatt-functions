using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Collections.Generic;
using System.Diagnostics;

namespace flatt_functions
{
    public class SearchInventoryCars
    {
        private readonly ILogger<SearchInventoryCars> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        // Only cars (TypeID = 2) are searchable here.
        private const int CarTypeId = 2;

        // Type-ahead defaults: keep the result set small and fast.
        private const int DefaultLimit = 5;
        private const int MaxLimit = 5;
        private const int MinLimit = 3;
        private const int MinQueryLength = 2;

        public SearchInventoryCars(ILogger<SearchInventoryCars> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _connectionString = FunctionHelpers.ResolveConnectionString(configuration);
        }

        [Function("SearchInventoryCars")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search-cars")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            var ct = req.FunctionContext.CancellationToken;

            try
            {
                _logger.LogInformation("🔎 SearchInventoryCars function started - Request ID: {requestId}", Guid.NewGuid());

                FunctionHelpers.AddCors(response, "GET, OPTIONS");
                // Short cache so rapid keystrokes for the same term can be reused
                response.Headers.Add("Cache-Control", "public, max-age=30");

                var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var term = (queryParams["q"] ?? queryParams["query"] ?? string.Empty).Trim();

                var limit = DefaultLimit;
                if (int.TryParse(queryParams["limit"], out var requestedLimit))
                {
                    limit = Math.Clamp(requestedLimit, MinLimit, MaxLimit);
                }

                // Require a minimum length so we don't scan the whole table on the first keystroke
                if (term.Length < MinQueryLength)
                {
                    await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.OK, new
                    {
                        Query = term,
                        Count = 0,
                        Results = Array.Empty<SearchResult>(),
                        Message = $"Enter at least {MinQueryLength} characters to search.",
                        Timestamp = DateTime.UtcNow
                    }, ct);
                    return response;
                }

                _logger.LogInformation("🔍 Searching car inventory for term '{term}' (limit {limit})", term, limit);

                var dataTimer = Stopwatch.StartNew();
                var results = await SearchCars(term, limit, ct);
                dataTimer.Stop();

                _logger.LogInformation("✅ Car search for '{term}' returned {count} matches in {ms}ms",
                    term, results.Count, dataTimer.ElapsedMilliseconds);

                await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.OK, new
                {
                    Query = term,
                    Count = results.Count,
                    Results = results,
                    Timestamp = DateTime.UtcNow
                }, ct);

                stopwatch.Stop();
                _logger.LogInformation("🎉 Car search completed in {totalMs}ms", stopwatch.ElapsedMilliseconds);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in SearchInventoryCars after {ms}ms - {errorType}: {message}",
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);

                await FunctionHelpers.WriteErrorAsync(response, HttpStatusCode.InternalServerError,
                    "An internal server error occurred");
                return response;
            }
        }

        private async Task<List<SearchResult>> SearchCars(string term, int limit, CancellationToken ct)
        {
            var timer = Stopwatch.StartNew();
            var results = new List<SearchResult>();

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            // Escape LIKE wildcards in user input so they are treated literally.
            var escaped = term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
            var prefix = escaped + "%";        // matches that START with the term (best)
            var contains = "%" + escaped + "%"; // matches that CONTAIN the term anywhere

            // Relevance ranking:
            //   1 = StockNo / VIN prefix (someone typing an identifier)
            //   2 = Make/Model/Year starts with the term
            //   3 = term appears anywhere in the searchable fields
            // Lower rank sorts first; ties break by newest unit.
            var query = @"
                SELECT TOP (@Limit)
                       [UnitID]
                      ,[StockNo]
                      ,[VIN]
                      ,[Make]
                      ,[Model]
                      ,[Year]
                      ,[Mileage]
                      ,[Price]
                      ,[Condition]
                      ,[Category]
                      ,[Status]
                      ,CASE
                          WHEN [StockNo] LIKE @Prefix OR [VIN] LIKE @Prefix THEN 1
                          WHEN [Make] LIKE @Prefix OR [Model] LIKE @Prefix
                               OR CAST([Year] AS NVARCHAR(8)) LIKE @Prefix THEN 2
                          ELSE 3
                       END AS MatchRank
                FROM [dbo].[Units]
                WHERE [TypeID] = @TypeID
                  AND (
                       [StockNo] LIKE @Contains
                    OR [VIN] LIKE @Contains
                    OR [Make] LIKE @Contains
                    OR [Model] LIKE @Contains
                    OR CAST([Year] AS NVARCHAR(8)) LIKE @Contains
                    OR [Category] LIKE @Contains
                    OR [Description] LIKE @Contains
                  )
                ORDER BY MatchRank ASC, [UpdatedAt] DESC, [UnitID] DESC";

            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@Limit", limit);
            command.Parameters.AddWithValue("@TypeID", CarTypeId);
            command.Parameters.AddWithValue("@Prefix", prefix);
            command.Parameters.AddWithValue("@Contains", contains);
            command.CommandTimeout = 15;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var make = reader["Make"] as string;
                var model = reader["Model"] as string;
                var year = reader["Year"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["Year"]);

                var labelParts = new[] { year?.ToString(), make, model }
                    .Where(p => !string.IsNullOrWhiteSpace(p));
                var label = string.Join(" ", labelParts);

                results.Add(new SearchResult
                {
                    UnitID = Convert.ToInt32(reader["UnitID"]),
                    Label = string.IsNullOrWhiteSpace(label) ? (reader["StockNo"] as string) : label,
                    StockNo = reader["StockNo"] as string,
                    VIN = reader["VIN"] as string,
                    Make = make,
                    Model = model,
                    Year = year,
                    Mileage = reader["Mileage"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["Mileage"]),
                    Price = reader["Price"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["Price"]),
                    Condition = reader["Condition"] as string,
                    Category = reader["Category"] as string,
                    Status = reader["Status"] as string
                });
            }

            timer.Stop();
            return results;
        }
    }
}
