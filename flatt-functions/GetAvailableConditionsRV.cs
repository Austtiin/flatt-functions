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
    public class GetAvailableConditionsRV
    {
        private readonly ILogger<GetAvailableConditionsRV> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        // Only RVs (TypeID = 1) are considered.
        private const int RvTypeId = 1;

        public GetAvailableConditionsRV(ILogger<GetAvailableConditionsRV> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _connectionString = FunctionHelpers.ResolveConnectionString(configuration);
        }

        [Function("GetAvailableConditionsRV")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rv/conditions")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            var ct = req.FunctionContext.CancellationToken;

            try
            {
                _logger.LogInformation("🏷️ GetAvailableConditionsRV function started - Request ID: {requestId}", Guid.NewGuid());

                FunctionHelpers.AddCors(response, "GET, OPTIONS");
                // Conditions change rarely; cache for 10 minutes
                response.Headers.Add("Cache-Control", "public, max-age=600, s-maxage=600");

                _logger.LogInformation("🔍 Fetching distinct conditions for RVs (TypeID = {typeId})", RvTypeId);

                var dataTimer = Stopwatch.StartNew();
                var conditions = await GetDistinctConditions(ct);
                dataTimer.Stop();

                _logger.LogInformation("✅ Retrieved {count} conditions in {ms}ms", conditions.Count, dataTimer.ElapsedMilliseconds);

                await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.OK, new
                {
                    Count = conditions.Count,
                    Conditions = conditions,
                    Timestamp = DateTime.UtcNow
                }, ct);

                stopwatch.Stop();
                _logger.LogInformation("🎉 Request completed in {totalMs}ms", stopwatch.ElapsedMilliseconds);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in GetAvailableConditionsRV after {ms}ms - {errorType}: {message}",
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);

                await FunctionHelpers.WriteErrorAsync(response, HttpStatusCode.InternalServerError,
                    "An internal server error occurred");
                return response;
            }
        }

        private async Task<List<string>> GetDistinctConditions(CancellationToken ct)
        {
            var results = new List<string>();

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);

            // Distinct, non-empty conditions for RVs, sorted alphabetically.
            var query = @"
                SELECT DISTINCT [Condition]
                FROM [dbo].[Units]
                WHERE [TypeID] = @TypeID
                  AND [Condition] IS NOT NULL
                  AND LTRIM(RTRIM([Condition])) <> ''
                ORDER BY [Condition] ASC";

            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TypeID", RvTypeId);
            command.CommandTimeout = 30;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var condition = reader["Condition"] as string;
                if (!string.IsNullOrWhiteSpace(condition))
                {
                    results.Add(condition.Trim());
                }
            }

            return results;
        }
    }
}
