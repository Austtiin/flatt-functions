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
    public class GetAvailableTypesRV
    {
        private readonly ILogger<GetAvailableTypesRV> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        // Only RVs (TypeID = 1) are considered.
        private const int RvTypeId = 1;

        public GetAvailableTypesRV(ILogger<GetAvailableTypesRV> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _connectionString = FunctionHelpers.ResolveConnectionString(configuration);
        }

        [Function("GetAvailableTypesRV")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rv/types")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            var ct = req.FunctionContext.CancellationToken;

            try
            {
                _logger.LogInformation("🗂️ GetAvailableTypesRV function started - Request ID: {requestId}", Guid.NewGuid());

                FunctionHelpers.AddCors(response, "GET, OPTIONS");
                // Categories change rarely; cache for 10 minutes
                response.Headers.Add("Cache-Control", "public, max-age=600, s-maxage=600");

                _logger.LogInformation("🔍 Fetching distinct categories for RVs (TypeID = {typeId})", RvTypeId);

                var dataTimer = Stopwatch.StartNew();
                var categories = await GetDistinctCategories(ct);
                dataTimer.Stop();

                _logger.LogInformation("✅ Retrieved {count} categories in {ms}ms", categories.Count, dataTimer.ElapsedMilliseconds);

                await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.OK, new
                {
                    Count = categories.Count,
                    Categories = categories,
                    Timestamp = DateTime.UtcNow
                }, ct);

                stopwatch.Stop();
                _logger.LogInformation("🎉 Request completed in {totalMs}ms", stopwatch.ElapsedMilliseconds);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in GetAvailableTypesRV after {ms}ms - {errorType}: {message}",
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);

                await FunctionHelpers.WriteErrorAsync(response, HttpStatusCode.InternalServerError,
                    "An internal server error occurred");
                return response;
            }
        }

        private async Task<List<string>> GetDistinctCategories(CancellationToken ct)
        {
            var results = new List<string>();

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            // Distinct, non-empty categories for RVs, sorted alphabetically.
            var query = @"
                SELECT DISTINCT [Category]
                FROM [dbo].[Units]
                WHERE [TypeID] = @TypeID
                  AND [Category] IS NOT NULL
                  AND LTRIM(RTRIM([Category])) <> ''
                ORDER BY [Category] ASC";

            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TypeID", RvTypeId);
            command.CommandTimeout = 30;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var category = reader["Category"] as string;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    results.Add(category.Trim());
                }
            }

            return results;
        }
    }
}
