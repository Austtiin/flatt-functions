using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Dynamic;
using System;

namespace flatt_functions
{
    public class GetFeatures
    {
        private readonly ILogger<GetFeatures> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public GetFeatures(ILogger<GetFeatures> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            var connectionString = configuration["SqlConnectionString"] ??
                                     configuration.GetConnectionString("SqlConnectionString") ??
                                     configuration["ConnectionStrings:SqlConnectionString"];

            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("SqlConnectionString is null or empty in configuration");
                throw new InvalidOperationException("SqlConnectionString not set in configuration.");
            }

            _connectionString = connectionString;
        }

        [Function("GetFeatures")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "features")] HttpRequestData req)
        {
            var response = req.CreateResponse();
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            try
            {
                _logger.LogInformation("📋 GetFeatures started");

                var results = new List<dynamic>();
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = "SELECT * FROM [dbo].[FeatureList]";
                using var command = new SqlCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync();

                var columnNames = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columnNames.Add(reader.GetName(i));
                }

                while (await reader.ReadAsync())
                {
                    var row = new ExpandoObject() as IDictionary<string, object?>;
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    results.Add(row);
                }

                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                var json = JsonSerializer.Serialize(results, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                await response.WriteStringAsync(json);
                _logger.LogInformation("✅ GetFeatures returning {count} rows", results.Count);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in GetFeatures: {message}", ex.Message);
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Error = true,
                    Message = "An internal server error occurred while retrieving features",
                    Details = ex.Message
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                return response;
            }
        }
    }
}
