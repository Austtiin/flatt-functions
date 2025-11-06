using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Collections.Generic;
using System.Dynamic;
using System.Diagnostics;

namespace flatt_functions
{
    public class GrabInventoryAll
    {
        private readonly ILogger<GrabInventoryAll> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public GrabInventoryAll(ILogger<GrabInventoryAll> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            // Enhanced connection string debugging
            // Try different ways to get the connection string
            var connectionString = configuration["SqlConnectionString"] ?? 
                                  configuration.GetConnectionString("SqlConnectionString") ??
                                  configuration["ConnectionStrings:SqlConnectionString"];
            
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("SqlConnectionString is null or empty in configuration");
                _logger.LogError("Available configuration keys: {keys}", 
                    string.Join(", ", configuration.AsEnumerable().Select(x => x.Key)));
                throw new InvalidOperationException("SqlConnectionString not set in configuration.");
            
            }
            
            // Log connection string details (safely)
            var connStringBuilder = new SqlConnectionStringBuilder(connectionString);
            _logger.LogInformation("Database connection configured - Server: {server}, Database: {database}, Timeout: {timeout}s", 
                connStringBuilder.DataSource, 
                connStringBuilder.InitialCatalog, 
                connStringBuilder.ConnectTimeout);
            
            _connectionString = connectionString;
        }

        [Function("GrabInventoryAll")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("🚀 GrabInventoryAll function started - Request ID: {requestId}", Guid.NewGuid());
                _logger.LogInformation("Request URL: {url}", req.Url);
                _logger.LogInformation("Request Method: {method}", req.Method);
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Enhanced query parameter debugging
                var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tableName = queryParams["table"] ?? "Units";
                var format = queryParams["format"] ?? "json";
                var includeSchema = queryParams["schema"] == "true";
                
                _logger.LogInformation("Query parameters - Table: {table}, Format: {format}, IncludeSchema: {includeSchema}", 
                    tableName, format, includeSchema);
                
                // Test connection with detailed timing
                _logger.LogInformation("🔗 Testing database connection...");
                var connectionTimer = Stopwatch.StartNew();
                await TestConnection();
                connectionTimer.Stop();
                _logger.LogInformation("✅ Database connection successful in {ms}ms", connectionTimer.ElapsedMilliseconds);
                
                // Discover database schema with timing
                _logger.LogInformation("🔍 Discovering database schema...");
                var schemaTimer = Stopwatch.StartNew();
                var schemaInfo = await DiscoverDatabaseSchema();
                schemaTimer.Stop();
                _logger.LogInformation("✅ Schema discovery completed in {ms}ms - Found {count} tables", 
                    schemaTimer.ElapsedMilliseconds, schemaInfo.Tables.Count);
                
                if (schemaInfo.Tables.Count == 0)
                {
                    _logger.LogWarning("⚠️ No tables found in database schema");
                }
                else
                {
                    _logger.LogInformation("📋 Available tables: {tables}", string.Join(", ", schemaInfo.Tables));
                }
                
                // Validate table exists
                if (!schemaInfo.Tables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("⚠️ Requested table '{table}' not found in schema. Available tables: {availableTables}", 
                        tableName, string.Join(", ", schemaInfo.Tables));
                }
                
                // Get table data with detailed timing and progress
                _logger.LogInformation("📊 Querying table: {table} (ALL DATA - no filters)...", tableName);
                var dataTimer = Stopwatch.StartNew();
                var tableData = await GetTableDataDynamic(tableName);
                dataTimer.Stop();
                _logger.LogInformation("✅ Data retrieval completed in {ms}ms - Retrieved {count} records from {table}", 
                    dataTimer.ElapsedMilliseconds, tableData.Count, tableName);
                
                if (tableData.Count == 0)
                {
                    _logger.LogWarning("⚠️ No data found in table '{table}'", tableName);
                }
                
                // Prepare response with timing
                _logger.LogInformation("📦 Preparing response...");
                var responseTimer = Stopwatch.StartNew();
                
                object responseData;
                if (includeSchema)
                {
                    responseData = new
                    {
                        Debug = new
                        {
                            RequestId = Guid.NewGuid(),
                            ProcessingTime = new
                            {
                                ConnectionMs = connectionTimer.ElapsedMilliseconds,
                                SchemaDiscoveryMs = schemaTimer.ElapsedMilliseconds,
                                DataRetrievalMs = dataTimer.ElapsedMilliseconds,
                                TotalMs = stopwatch.ElapsedMilliseconds
                            },
                            RequestInfo = new
                            {
                                Url = req.Url.ToString(),
                                Method = req.Method,
                                QueryParameters = queryParams.AllKeys.ToDictionary(k => k, k => queryParams[k])
                            }
                        },
                        Schema = schemaInfo,
                        Data = tableData,
                        RequestedTable = tableName,
                        RecordCount = tableData.Count,
                        Timestamp = DateTime.UtcNow,
                        DataFilter = "ALL (no TypeID filter applied)"
                    };
                }
                else
                {
                    responseData = tableData;
                }
                
                if (format.ToLower() == "html")
                {
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "text/html; charset=utf-8");
                    // Cache stable inventory responses for 30 days
                    response.Headers.Add("Cache-Control", "public, max-age=2592000, s-maxage=2592000");
                    var html = GenerateHtmlResponse(responseData, tableName, schemaInfo, stopwatch.ElapsedMilliseconds);
                    await response.WriteStringAsync(html);
                }
                else
                {
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    // Cache stable inventory responses for 30 days
                    response.Headers.Add("Cache-Control", "public, max-age=2592000, s-maxage=2592000");
                    
                    var jsonOptions = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    };
                    
                    var jsonResponse = JsonSerializer.Serialize(responseData, jsonOptions);
                    await response.WriteStringAsync(jsonResponse);
                }
                
                responseTimer.Stop();
                stopwatch.Stop();
                _logger.LogInformation("🎉 Response completed successfully in {totalMs}ms (Response prep: {responseMs}ms)", 
                    stopwatch.ElapsedMilliseconds, responseTimer.ElapsedMilliseconds);
                
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in GrabInventoryAll function after {ms}ms - {errorType}: {message}", 
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                
                // Enhanced error response with debugging info
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var errorResponse = new
                {
                    Error = true,
                    Message = ex.Message,
                    Type = ex.GetType().Name,
                    RequestId = Guid.NewGuid(),
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    RequestInfo = new
                    {
                        Url = req.Url.ToString(),
                        Method = req.Method
                    }
                };
                
                var errorJson = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
                
                await response.WriteStringAsync(errorJson);
                return response;
            }
        }

        private async Task TestConnection()
        {
            var timer = Stopwatch.StartNew();
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Test with a simple query
                using var command = new SqlCommand("SELECT 1", connection);
                var result = await command.ExecuteScalarAsync();
                
                timer.Stop();
                _logger.LogInformation("✅ Database connection test successful in {ms}ms", timer.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Database connection test failed after {ms}ms - {errorType}: {message}", 
                    timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                throw;
            }
        }

        private async Task<DatabaseSchema> DiscoverDatabaseSchema()
        {
            var timer = Stopwatch.StartNew();
            var schema = new DatabaseSchema();
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Get table names
                var tablesQuery = @"
                    SELECT TABLE_NAME 
                    FROM INFORMATION_SCHEMA.TABLES 
                    WHERE TABLE_TYPE = 'BASE TABLE'
                    ORDER BY TABLE_NAME";
                
                using var tablesCommand = new SqlCommand(tablesQuery, connection);
                using var tablesReader = await tablesCommand.ExecuteReaderAsync();
                
                while (await tablesReader.ReadAsync())
                {
                    schema.Tables.Add(tablesReader.GetString("TABLE_NAME"));
                }
                
                timer.Stop();
                _logger.LogInformation("✅ Database schema discovery completed in {ms}ms - Found {count} tables", 
                    timer.ElapsedMilliseconds, schema.Tables.Count);
                
                return schema;
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Schema discovery failed after {ms}ms - {errorType}: {message}", 
                    timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                throw;
            }
        }

        private async Task<List<dynamic>> GetTableDataDynamic(string tableName)
        {
            var results = new List<dynamic>();
            var timer = Stopwatch.StartNew();
            
            _logger.LogInformation("📊 Starting data retrieval from table: {tableName} (ALL DATA - no TypeID filter)", tableName);
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Sanitize table name to prevent SQL injection
                var sanitizedTableName = tableName.Replace("[", "").Replace("]", "").Replace("'", "").Replace("\"", "");
                
                // Query ALL data without any TypeID filter
                string query = $"SELECT * FROM [{sanitizedTableName}]";
                _logger.LogInformation("📝 Executing query: {query} (retrieving ALL records)", query);
        
                using var command = new SqlCommand(query, connection);
                command.CommandTimeout = 30; // 30 second timeout
                using var reader = await command.ExecuteReaderAsync();
                
                // Get column names
                var columnNames = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columnNames.Add(reader.GetName(i));
                }
                
                _logger.LogInformation("📋 Query returned {columnCount} columns: {columns}", 
                    columnNames.Count, string.Join(", ", columnNames));
                
                // Read data with progress logging
                var rowCount = 0;
                while (await reader.ReadAsync())
                {
                    var row = new ExpandoObject() as IDictionary<string, object>;
                    
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        row[columnNames[i]] = value;
                    }
                    
                    results.Add(row);
                    rowCount++;
                    
                    // Log progress for large datasets
                    if (rowCount % 1000 == 0)
                    {
                        _logger.LogInformation("📊 Processed {rowCount} rows...", rowCount);
                    }
                }
                
                timer.Stop();
                
                if (results.Count == 0)
                {
                    _logger.LogWarning("⚠️ Query returned no data from table '{tableName}' in {ms}ms", 
                        tableName, timer.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogInformation("✅ Successfully retrieved {count} records from {tableName} in {ms}ms (ALL DATA - no filter)", 
                        results.Count, tableName, timer.ElapsedMilliseconds);
                }
                
                return results;
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Data retrieval failed from table '{tableName}' after {ms}ms - {errorType}: {message}", 
                    tableName, timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                throw;
            }
        }

        private string GenerateHtmlResponse(object data, string tableName, DatabaseSchema schema, long processingTimeMs)
        {
            var html = $@"
            <!DOCTYPE html>
            <html>
            <head>
                <title>GrabInventoryAll - Table: {tableName}</title>
                <style>
                    body {{ font-family: Arial, sans-serif; margin: 20px; }}
                    .header {{ background-color: #f0f0f0; padding: 15px; border-radius: 5px; margin-bottom: 20px; }}
                    .info {{ color: #666; margin-bottom: 10px; }}
                    .data-filter {{ color: #28a745; font-weight: bold; }}
                    table {{ border-collapse: collapse; width: 100%; }}
                    th, td {{ border: 1px solid #ddd; padding: 8px; text-align: left; }}
                    th {{ background-color: #f2f2f2; }}
                    .stats {{ background-color: #e9ecef; padding: 10px; border-radius: 5px; margin: 20px 0; }}
                </style>
            </head>
            <body>
                <div class='header'>
                    <h1>🔍 GrabInventoryAll Function Results</h1>
                    <div class='info'>Table: <strong>{tableName}</strong></div>
                    <div class='info'>Processing Time: <strong>{processingTimeMs}ms</strong></div>
                    <div class='data-filter'>Data Filter: ALL RECORDS (no TypeID filter applied)</div>
                    <div class='info'>Generated: <strong>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</strong></div>
                </div>";
            
            if (data is List<dynamic> tableData)
            {
                html += $@"
                <div class='stats'>
                    <strong>Record Count:</strong> {tableData.Count}
                </div>";
                
                if (tableData.Count > 0)
                {
                    html += "<table><thead><tr>";
                    
                    // Get column headers from first row
                    var firstRow = tableData[0] as IDictionary<string, object>;
                    foreach (var column in firstRow.Keys)
                    {
                        html += $"<th>{column}</th>";
                    }
                    html += "</tr></thead><tbody>";
                    
                    // Add data rows (limit to first 100 for display)
                    var displayCount = Math.Min(tableData.Count, 100);
                    for (int i = 0; i < displayCount; i++)
                    {
                        html += "<tr>";
                        var row = tableData[i] as IDictionary<string, object>;
                        foreach (var value in row.Values)
                        {
                            html += $"<td>{value?.ToString() ?? "NULL"}</td>";
                        }
                        html += "</tr>";
                    }
                    
                    html += "</tbody></table>";
                    
                    if (tableData.Count > 100)
                    {
                        html += $"<p><em>Showing first 100 of {tableData.Count} records. Use JSON format for complete data.</em></p>";
                    }
                }
                else
                {
                    html += "<p>No data found in table.</p>";
                }
            }
            
            html += @"
                <div class='stats'>
                    <h3>Available Tables in Database:</h3>
                    <ul>";
            
            foreach (var table in schema.Tables)
            {
                html += $"<li>{table}</li>";
            }
            
            html += @"
                    </ul>
                </div>
            </body>
            </html>";
            
            return html;
        }
    }
}