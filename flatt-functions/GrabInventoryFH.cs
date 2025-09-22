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
    public class GrabInventory
    {
        private readonly ILogger<GrabInventory> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public GrabInventory(ILogger<GrabInventory> logger, IConfiguration configuration)
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

        [Function("GrabInventory")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("🚀 GrabInventory function started - Request ID: {requestId}", Guid.NewGuid());
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
                _logger.LogInformation("📊 Querying table: {table}...", tableName);
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
                        Timestamp = DateTime.UtcNow
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
                    var html = GenerateHtmlResponse(responseData, tableName, schemaInfo, stopwatch.ElapsedMilliseconds);
                    await response.WriteStringAsync(html);
                }
                else
                {
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
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
                _logger.LogError(ex, "❌ Error in GrabInventory function after {ms}ms - {errorType}: {message}", 
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                
                // Enhanced error response with debugging info
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var errorResponse = new
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    ErrorType = ex.GetType().Name,
                    Timestamp = DateTime.UtcNow,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    Debug = new
                    {
                        RequestUrl = req.Url.ToString(),
                        RequestMethod = req.Method,
                        ConnectionString = _connectionString?.Length > 0 ? "Configured" : "Missing",
                        InnerException = ex.InnerException?.Message
                    },
                    StackTrace = ex.StackTrace
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
            _logger.LogInformation("🔌 Opening database connection...");
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                timer.Stop();
                
                _logger.LogInformation("✅ Database connection opened successfully in {ms}ms - State: {state}", 
                    timer.ElapsedMilliseconds, connection.State);
                
                // Test with a simple query
                var testTimer = Stopwatch.StartNew();
                using var testCmd = new SqlCommand("SELECT 1", connection);
                var result = await testCmd.ExecuteScalarAsync();
                testTimer.Stop();
                
                _logger.LogInformation("✅ Test query executed successfully in {ms}ms - Result: {result}", 
                    testTimer.ElapsedMilliseconds, result);
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Database connection failed after {ms}ms - {errorType}: {message}", 
                    timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                throw;
            }
        }

        private async Task<DatabaseSchema> DiscoverDatabaseSchema()
        {
            var schema = new DatabaseSchema();
            var timer = Stopwatch.StartNew();
            
            _logger.LogInformation("🔍 Starting schema discovery...");
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Get all tables with enhanced logging
                var tablesQuery = @"
                    SELECT TABLE_NAME, TABLE_TYPE 
                    FROM INFORMATION_SCHEMA.TABLES 
                    WHERE TABLE_TYPE = 'BASE TABLE' 
                    ORDER BY TABLE_NAME";
                    
                _logger.LogInformation("📋 Executing tables query...");
                using var tablesCmd = new SqlCommand(tablesQuery, connection);
                using var tablesReader = await tablesCmd.ExecuteReaderAsync();
                
                while (await tablesReader.ReadAsync())
                {
                    var tableName = tablesReader["TABLE_NAME"].ToString();
                    if (!string.IsNullOrEmpty(tableName))
                    {
                        schema.Tables.Add(tableName);
                        _logger.LogDebug("Found table: {tableName}", tableName);
                    }
                }
                tablesReader.Close();
                
                _logger.LogInformation("📊 Found {count} tables in {ms}ms", schema.Tables.Count, timer.ElapsedMilliseconds);
                
                // Get columns for each table with progress logging
                var tableIndex = 0;
                foreach (var tableName in schema.Tables)
                {
                    tableIndex++;
                    _logger.LogDebug("🔍 Analyzing table {current}/{total}: {tableName}", 
                        tableIndex, schema.Tables.Count, tableName);
                    
                    var columnsQuery = @"
                        SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT
                        FROM INFORMATION_SCHEMA.COLUMNS 
                        WHERE TABLE_NAME = @TableName 
                        ORDER BY ORDINAL_POSITION";
                        
                    using var columnsCmd = new SqlCommand(columnsQuery, connection);
                    columnsCmd.Parameters.AddWithValue("@TableName", tableName);
                    using var columnsReader = await columnsCmd.ExecuteReaderAsync();
                    
                    var columns = new List<ColumnInfo>();
                    while (await columnsReader.ReadAsync())
                    {
                        columns.Add(new ColumnInfo
                        {
                            Name = columnsReader["COLUMN_NAME"].ToString() ?? string.Empty,
                            DataType = columnsReader["DATA_TYPE"].ToString() ?? string.Empty,
                            IsNullable = columnsReader["IS_NULLABLE"].ToString() == "YES",
                            DefaultValue = columnsReader["COLUMN_DEFAULT"]?.ToString()
                        });
                    }
                    schema.TableColumns[tableName] = columns;
                    columnsReader.Close();
                    
                    _logger.LogDebug("✅ Table {tableName} has {columnCount} columns", tableName, columns.Count);
                }
                
                timer.Stop();
                _logger.LogInformation("✅ Schema discovery completed in {ms}ms", timer.ElapsedMilliseconds);
                
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
            
            _logger.LogInformation("📊 Starting data retrieval from table: {tableName}", tableName);
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Sanitize table name to prevent SQL injection
                var sanitizedTableName = tableName.Replace("[", "").Replace("]", "").Replace("'", "").Replace("\"", "");
                
                // Check if TypeID column exists first
                var columnCheckQuery = @"
                    SELECT COUNT(*) 
                    FROM INFORMATION_SCHEMA.COLUMNS 
                    WHERE TABLE_NAME = @TableName AND COLUMN_NAME = 'TypeID'";
        
                using var columnCheckCmd = new SqlCommand(columnCheckQuery, connection);
                columnCheckCmd.Parameters.AddWithValue("@TableName", sanitizedTableName);
                var hasTypeIdColumn = (int)await columnCheckCmd.ExecuteScalarAsync() > 0;
        
                // Build query with or without TypeID filter
                string query;
                if (hasTypeIdColumn)
                {
                    query = $"SELECT * FROM [{sanitizedTableName}] WHERE TypeID = @TypeID";
                    _logger.LogInformation("📝 Executing filtered query: {query} (TypeID = 1)", query);
                }
                else
                {
                    query = $"SELECT * FROM [{sanitizedTableName}]";
                    _logger.LogWarning("⚠️ TypeID column not found in table '{tableName}', returning all records", tableName);
                    _logger.LogInformation("📝 Executing query: {query}", query);
                }
        
                using var command = new SqlCommand(query, connection);
                if (hasTypeIdColumn)
                {
                    command.Parameters.AddWithValue("@TypeID", 1);
                }
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
                    var filterMessage = hasTypeIdColumn ? " (filtered by TypeID = 1)" : "";
                    _logger.LogInformation("✅ Successfully retrieved {count} records from {tableName} in {ms}ms{filter}", 
                        results.Count, tableName, timer.ElapsedMilliseconds, filterMessage);
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
            var jsonData = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            
            var tablesDisplay = schema?.Tables?.Any() == true 
                ? string.Join(", ", schema.Tables) 
                : "No tables found";
            
            return $@"
<!DOCTYPE html>
<html>
<head>
    <title>Dynamic Database Explorer - Debug Mode</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; background: #f5f5f5; }}
        .container {{ max-width: 1200px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; }}
        .success {{ color: green; font-weight: bold; font-size: 18px; }}
        .debug {{ background: #f8f9fa; padding: 15px; border-radius: 4px; margin: 20px 0; border-left: 4px solid #007bff; }}
        .table-info {{ background: #e8f5e8; padding: 15px; border-radius: 4px; margin: 20px 0; }}
        .schema-info {{ background: #f0f8ff; padding: 15px; border-radius: 4px; margin: 20px 0; }}
        pre {{ background: #f8f8f8; padding: 15px; border-radius: 4px; overflow-x: auto; border: 1px solid #ddd; max-height: 400px; }}
        .api-info {{ background: #fff3cd; padding: 15px; border-radius: 4px; margin: 20px 0; }}
        .performance {{ background: #e7f3ff; padding: 15px; border-radius: 4px; margin: 20px 0; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>🗄️ Dynamic Database Explorer - Debug Mode</h1>
        <div class='success'>✅ Database connection successful!</div>
        
        <div class='debug'>
            <h2>🐛 Debug Information</h2>
            <p><strong>Total Processing Time:</strong> {processingTimeMs}ms</p>
            <p><strong>Request Time:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
            <p><strong>Server:</strong> {Environment.MachineName}</p>
            <p><strong>Function App:</strong> {Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "Local Development"}</p>
        </div>
        
        <div class='table-info'>
            <h2>📊 Current Query</h2>
            <p><strong>Table:</strong> {tableName ?? "Unknown"}</p>
            <p><strong>Tables Available:</strong> {schema?.Tables?.Count ?? 0}</p>
        </div>
        
        <div class='schema-info'>
            <h2>🗂️ Available Tables</h2>
            <p><strong>Tables Found:</strong> {tablesDisplay}</p>
        </div>
        
        <div class='api-info'>
            <h2>🔗 API Usage</h2>
            <ul>
                <li><strong>Default (Units):</strong> <code>/api/GrabInventory</code></li>
                <li><strong>Specific Table:</strong> <code>/api/GrabInventory?table=TableName</code></li>
                <li><strong>With Schema:</strong> <code>/api/GrabInventory?schema=true</code></li>
                <li><strong>HTML Format:</strong> <code>/api/GrabInventory?format=html</code></li>
                <li><strong>Debug Mode:</strong> <code>/api/GrabInventory?schema=true&format=html</code></li>
            </ul>
        </div>
        
        <h2>📋 Raw Data:</h2>
        <pre>{jsonData ?? "No data available"}</pre>
    </div>
</body>
</html>";
        }
    }

    public class DatabaseSchema
    {
        public List<string> Tables { get; set; } = new List<string>();
        public Dictionary<string, List<ColumnInfo>> TableColumns { get; set; } = new Dictionary<string, List<ColumnInfo>>();
    }

    public class ColumnInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public bool IsNullable { get; set; }
        public string? DefaultValue { get; set; }
    }
}