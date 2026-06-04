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
    public class GetFeaturedCars
    {
        private readonly ILogger<GetFeaturedCars> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        // Only cars (TypeID = 2) are eligible to be featured here.
        private const int CarTypeId = 2;
        private const int DefaultCount = 4;
        private const int MaxCount = 4;
        private const int MinCount = 3;

        public GetFeaturedCars(ILogger<GetFeaturedCars> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _connectionString = FunctionHelpers.ResolveConnectionString(configuration);
        }

        [Function("GetFeaturedCars")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "featured-cars")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            var ct = req.FunctionContext.CancellationToken;

            try
            {
                _logger.LogInformation("⭐ GetFeaturedCars function started - Request ID: {requestId}", Guid.NewGuid());

                FunctionHelpers.AddCors(response, "GET, OPTIONS");

                // Allow caller to request 3 or 4 featured cars (defaults to 4, clamped to valid range)
                var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var count = DefaultCount;
                if (int.TryParse(queryParams["count"], out var requestedCount))
                {
                    count = Math.Clamp(requestedCount, MinCount, MaxCount);
                }

                _logger.LogInformation("🔍 Selecting {count} random featured cars (TypeID = {typeId})", count, CarTypeId);

                var dataTimer = Stopwatch.StartNew();
                var featured = await GetRandomFeaturedCars(count, ct);
                dataTimer.Stop();

                _logger.LogInformation("✅ Retrieved {count} featured cars in {ms}ms", featured.Count, dataTimer.ElapsedMilliseconds);

                // Featured list is randomized per request, so it must not be cached
                response.Headers.Add("Cache-Control", "no-store");

                await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.OK, new
                {
                    Count = featured.Count,
                    FeaturedCars = featured,
                    Timestamp = DateTime.UtcNow
                }, ct);

                stopwatch.Stop();
                _logger.LogInformation("🎉 Request completed successfully in {totalMs}ms", stopwatch.ElapsedMilliseconds);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in GetFeaturedCars function after {ms}ms - {errorType}: {message}",
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);

                await FunctionHelpers.WriteErrorAsync(response, HttpStatusCode.InternalServerError,
                    "An internal server error occurred");

                return response;
            }
        }

        private async Task<List<FeaturedCar>> GetRandomFeaturedCars(int count, CancellationToken ct)
        {
            var timer = Stopwatch.StartNew();
            var results = new List<FeaturedCar>();
            var blobBaseUrl = FunctionHelpers.ResolveBlobBaseUrl(_configuration);

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(ct);

                // ORDER BY NEWID() returns a random sample; TOP is parameterized via
                // a guarded count and only TypeID = 2 (cars) are eligible.
                var query = @"
                    SELECT TOP (@Count)
                           [UnitID]
                          ,[StockNo]
                          ,[VIN]
                          ,[Make]
                          ,[Model]
                          ,[Year]
                          ,[Mileage]
                          ,[Price]
                          ,[MSRP]
                    FROM [dbo].[Units]
                    WHERE [TypeID] = @TypeID
                    ORDER BY NEWID()";

                _logger.LogInformation("📝 Executing featured car query (count: {count})", count);

                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@Count", count);
                command.Parameters.AddWithValue("@TypeID", CarTypeId);
                command.CommandTimeout = 30;

                using var reader = await command.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    var make = reader["Make"] as string;
                    var model = reader["Model"] as string;
                    var year = reader["Year"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["Year"]);
                    var vin = reader["VIN"] as string;

                    // Build a friendly display name from Year/Make/Model
                    var nameParts = new[] { year?.ToString(), make, model }
                        .Where(p => !string.IsNullOrWhiteSpace(p));
                    var name = string.Join(" ", nameParts);

                    results.Add(new FeaturedCar
                    {
                        UnitID = Convert.ToInt32(reader["UnitID"]),
                        Name = string.IsNullOrWhiteSpace(name) ? null : name,
                        StockNo = reader["StockNo"] as string,
                        Make = make,
                        Model = model,
                        Year = year,
                        Mileage = reader["Mileage"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["Mileage"]),
                        Price = reader["Price"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["Price"]),
                        MSRP = reader["MSRP"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["MSRP"]),
                        ThumbnailURL = FunctionHelpers.BuildThumbnailUrl(blobBaseUrl, vin)
                    });
                }

                timer.Stop();
                _logger.LogInformation("✅ Featured car query returned {count} rows in {ms}ms",
                    results.Count, timer.ElapsedMilliseconds);

                return results;
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Featured car retrieval failed after {ms}ms - {errorType}: {message}",
                    timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                throw;
            }
        }
    }

    public class FeaturedCar
    {
        public int UnitID { get; set; }
        public string? Name { get; set; }
        public string? StockNo { get; set; }
        public string? Make { get; set; }
        public string? Model { get; set; }
        public int? Year { get; set; }
        public int? Mileage { get; set; }
        public decimal? Price { get; set; }
        public decimal? MSRP { get; set; }
        public string? ThumbnailURL { get; set; }
    }
}
