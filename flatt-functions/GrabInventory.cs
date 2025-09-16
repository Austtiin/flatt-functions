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
            
            // Get connection string without debug logging
            _connectionString = configuration["SqlConnectionString"]
                ?? throw new InvalidOperationException("SqlConnectionString not set in configuration.");
        }

        [Function("GrabInventory")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var response = req.CreateResponse();
            
            try
            {
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Get query parameters
                var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tableName = queryParams["table"] ?? "Units";
                var format = queryParams["format"] ?? "json";
                var includeSchema = queryParams["schema"] == "true";
                
                // Test connection
                await TestConnection();
                
                // Discover database schema
                var schemaInfo = await DiscoverDatabaseSchema();
                _logger.LogInformation("Discovered {count} tables in database", schemaInfo.Tables.Count);
                
                // Get table data
                _logger.LogInformation("Querying table: {table}", tableName);
                var tableData = await GetTableDataDynamic(tableName);
                _logger.LogInformation("Retrieved {count} records from {table}", tableData.Count, tableName);
                
                // Prepare response
                object responseData;
                if (includeSchema)
                {
                    responseData = new
                    {
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
                    var html = GenerateHtmlResponse(responseData, tableName, schemaInfo);
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
                
                _logger.LogInformation("Response completed successfully");
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GrabInventory function");
                
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var errorResponse = new
                {
                    Success = false,
                    Message = $"Error: {ex.Message}",
                    Timestamp = DateTime.UtcNow,
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
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
        }

        private async Task<DatabaseSchema> DiscoverDatabaseSchema()
        {
            var schema = new DatabaseSchema();
            
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            // Get all tables
            var tablesQuery = @"
                SELECT TABLE_NAME, TABLE_TYPE 
                FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_TYPE = 'BASE TABLE' 
                ORDER BY TABLE_NAME";
                
            using var tablesCmd = new SqlCommand(tablesQuery, connection);
            using var tablesReader = await tablesCmd.ExecuteReaderAsync();
            
            while (await tablesReader.ReadAsync())
            {
                var tableName = tablesReader["TABLE_NAME"].ToString();
                if (!string.IsNullOrEmpty(tableName))
                {
                    schema.Tables.Add(tableName);
                }
            }
            tablesReader.Close();
            
            // Get columns for each table
            foreach (var tableName in schema.Tables)
            {
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
            }
            
            return schema;
        }

        private async Task<List<dynamic>> GetTableDataDynamic(string tableName)
        {
            var results = new List<dynamic>();
            
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            // Sanitize table name to prevent SQL injection
            var sanitizedTableName = tableName.Replace("[", "").Replace("]", "").Replace("'", "").Replace("\"", "");
            var query = $"SELECT * FROM [{sanitizedTableName}]";
            
            using var command = new SqlCommand(query, connection);
            using var reader = await command.ExecuteReaderAsync();
            
            // Get column names
            var columnNames = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columnNames.Add(reader.GetName(i));
            }
            
            // Read data
            while (await reader.ReadAsync())
            {
                var row = new ExpandoObject() as IDictionary<string, object>;
                
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[columnNames[i]] = value;
                }
                
                results.Add(row);
            }
            
            return results;
        }

        private string GenerateHtmlResponse(object data, string tableName, DatabaseSchema schema)
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
    <title>Dynamic Database Explorer</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; background: #f5f5f5; }}
        .container {{ max-width: 1200px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; }}
        .success {{ color: green; font-weight: bold; font-size: 18px; }}
        .table-info {{ background: #e8f5e8; padding: 15px; border-radius: 4px; margin: 20px 0; }}
        .schema-info {{ background: #f0f8ff; padding: 15px; border-radius: 4px; margin: 20px 0; }}
        pre {{ background: #f8f8f8; padding: 15px; border-radius: 4px; overflow-x: auto; border: 1px solid #ddd; }}
        .api-info {{ background: #fff3cd; padding: 15px; border-radius: 4px; margin: 20px 0; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>🗄️ Dynamic Database Explorer</h1>
        <div class='success'>✅ Database connection successful!</div>
        
        <div class='table-info'>
            <h2>📊 Current Query</h2>
            <p><strong>Table:</strong> {tableName ?? "Unknown"}</p>
            <p><strong>Query Time:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
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