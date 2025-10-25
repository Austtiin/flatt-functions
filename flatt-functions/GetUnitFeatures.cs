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
    public class GetUnitFeatures
    {
        private readonly ILogger<GetUnitFeatures> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public GetUnitFeatures(ILogger<GetUnitFeatures> logger, IConfiguration configuration)
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

        [Function("GetAllUnitFeatures")]
        public async Task<HttpResponseData> GetAll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "unit-features")] HttpRequestData req)
        {
            var response = req.CreateResponse();
            AddCors(response);

            try
            {
                _logger.LogInformation("📋 GetAllUnitFeatures started");

                var results = new List<dynamic>();
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                const string query = "SELECT * FROM [dbo].[UnitFeatures]";
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
                _logger.LogInformation("✅ GetAllUnitFeatures returning {count} rows", results.Count);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in GetAllUnitFeatures: {message}", ex.Message);
                return await ErrorResponse(req, HttpStatusCode.InternalServerError, new
                {
                    Error = true,
                    Message = "An internal server error occurred while retrieving unit features",
                    Details = ex.Message
                });
            }
        }

        [Function("GetUnitFeaturesForUnit")]
        public async Task<HttpResponseData> GetByUnit(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "units/{id:int}/features")] HttpRequestData req,
            string id)
        {
            var response = req.CreateResponse();
            AddCors(response);

            if (!int.TryParse(id, out var unitId) || unitId <= 0)
            {
                return await ErrorResponse(req, HttpStatusCode.BadRequest, new
                {
                    Error = true,
                    Message = "Invalid UnitID format. Must be a positive number.",
                    StatusCode = 400
                });
            }

            try
            {
                _logger.LogInformation("🔎 GetUnitFeaturesForUnit started for UnitID {unitId}", unitId);

                var results = new List<dynamic>();
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                const string query = "SELECT * FROM [dbo].[UnitFeatures] WHERE [UnitID] = @UnitID";
                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@UnitID", unitId);
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
                _logger.LogInformation("✅ GetUnitFeaturesForUnit returning {count} rows for UnitID {unitId}", results.Count, unitId);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in GetUnitFeaturesForUnit: {message}", ex.Message);
                return await ErrorResponse(req, HttpStatusCode.InternalServerError, new
                {
                    Error = true,
                    Message = "An internal server error occurred while retrieving unit features for the specified unit",
                    Details = ex.Message
                });
            }
        }

        private static void AddCors(HttpResponseData response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        private static async Task<HttpResponseData> ErrorResponse(HttpRequestData req, HttpStatusCode code, object payload)
        {
            var res = req.CreateResponse(code);
            AddCors(res);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
            return res;
        }
    }
}
