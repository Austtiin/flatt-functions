# 🚀 Flatt Inventory Management API

**Azure Functions-based REST API** for managing inventory of vehicles, fish houses, and trailers with comprehensive admin panel support.

## 📋 Table of Contents
- [Overview](#overview)
- [Tech Stack](#tech-stack)
- [Getting Started](#getting-started)
- [API Endpoints](#api-endpoints)
  - [Inventory Retrieval](#inventory-retrieval)
  - [Data Management](#data-management)
  - [Health & Validation](#health--validation)
  - [Dashboard & Reports](#dashboard--reports)
  - [AI Utilities](#ai-utilities)
- [Data Models](#data-models)
- [Error Handling](#error-handling)
- [Development](#development)

---

## 🎯 Overview

This API provides a complete backend solution for inventory management, featuring:
- ✅ CRUD operations for inventory items
- ✅ Real-time database health monitoring
- ✅ Duplicate detection (VIN/StockNo validation)
- ✅ Advanced reporting and dashboard statistics
- ✅ Type-specific filtering (Vehicles, Fish Houses, Trailers)
- ✅ CORS-enabled for web admin panels

**Base URL (Local):** `http://localhost:7071/api`  
**Database:** Azure SQL Database (`flatt-inv-sql`)

---

## 🛠 Tech Stack

- **Runtime:** .NET 8.0 (Isolated Process Model)
- **Framework:** Azure Functions v4
- **Database:** Azure SQL Database
- **ORM:** Microsoft.Data.SqlClient (ADO.NET)
- **Auth:** Anonymous (to be secured in production)
- **CORS:** Enabled for all origins

---

## 🚦 Getting Started

### Prerequisites
- .NET 8.0 SDK
- Azure Functions Core Tools v4
- Azure SQL Database connection string

### Local Development
```bash
# Clone the repository
git clone https://github.com/Austtiin/flatt-functions.git
cd flatt-functions/flatt-functions

# Configure connection string
# Edit local.settings.json and add:
{
  "Values": {
    "SqlConnectionString": "Server=tcp:flatt-db-server.database.windows.net,1433;..."
  }
}

# Build and run
dotnet build
func start

# API will be available at http://localhost:7071
```

---

# 📡 API Endpoints

## 🔍 Inventory Retrieval

### 1. Get All Inventory
**Endpoint:** `GET /api/GrabInventoryAll`

**Description:** Retrieves all inventory items from the database without any filters.

**Use Case:** Display complete inventory list in admin panel.

**Request:**
```http
GET /api/GrabInventoryAll HTTP/1.1
Host: localhost:7071
```

**Response (200 OK):**
```json
[
  {
    "unitID": 1,
    "vin": "5TJBE51111",
    "stockNo": "IC1111",
    "make": "Ice Castle Fish House",
    "model": "Extreme III",
    "year": 2024,
    "condition": "New",
    "description": null,
    
    "category": "RV",
    "typeID": 1,
    "widthCategory": "8",
    "sizeCategory": "21",
    "price": 54950.00,
    "status": "Available",
    "createdAt": "2025-10-17T02:38:34.9566667",
    "updatedAt": "2025-10-17T02:38:34.9629574"
  }
]
```

---

### 2. Get Fish Houses Only
**Endpoint:** `GET /api/GrabInventoryFH`

**Description:** Retrieves only Fish House inventory (TypeID = 1).

**Use Case:** Display fish house catalog on website.

**Request:**
```http
GET /api/GrabInventoryFH HTTP/1.1
Host: localhost:7071
```

**Response (200 OK):**
```json
[
  {
    "unitID": 1,
    "typeID": 1,
    "make": "Ice Castle Fish House",
    "model": "Extreme III",
    "price": 54950.00,
    ...
  }
]
```

---

### 3. Get Vehicles Only
**Endpoint:** `GET /api/GrabInventoryVH`

**Description:** Retrieves only Vehicle inventory (TypeID = 2).

**Use Case:** Display vehicle catalog on website.

**Request:**
```http
GET /api/GrabInventoryVH HTTP/1.1
Host: localhost:7071
```

**Response (200 OK):**
```json
[
  {
    "unitID": 5,
    "typeID": 2,
    "make": "Honda",
    "model": "Civic",
    "price": 25000.00,
    ...
  }
]
```

**Note:** Trailers (TypeID = 3) would require a similar endpoint if needed.

---

### 4. Get Single Item by ID
**Endpoint:** `GET /api/vehicles/{id}`

**Description:** Retrieves a single inventory item by its UnitID.

**Use Case:** Display detailed vehicle information page.

**URL Parameters:**
- `id` (required, integer) - The UnitID to retrieve

**Request:**
```http
GET /api/vehicles/1 HTTP/1.1
Host: localhost:7071
```

**Response (200 OK):**
```json
{
  "unitID": 1,
  "vin": "5TJBE51111",
  "stockNo": "IC1111",
  "make": "Ice Castle Fish House",
  "model": "Extreme III",
  "year": 2024,
  "price": 54950.00,
  "status": "Available",
  ...
}
```

**Response (404 Not Found):**
```json
{
  "error": true,
  "message": "Vehicle with UnitID 1 not found",
  "statusCode": 404
}
```

**Response (400 Bad Request):**
```json
{
  "error": true,
  "message": "Invalid vehicle ID format. Must be a number.",
  "statusCode": 400
}
```

---

## ✏️ Data Management

### 5. Add New Inventory
**Endpoint:** `POST /api/vehicles/add`

**Description:** Creates a new inventory item in the database.

**Use Case:** Admin adds new vehicle/fish house/trailer to inventory.

**Required Fields:**
- `vin` (string) - Vehicle Identification Number
- `year` (integer) - Year of manufacture
- `make` (string) - Manufacturer name
- `model` (string) - Model name
- `typeId` (integer) - 1=Fish House, 2=Vehicle, 3=Trailer
- `price` (decimal) - Price in dollars
- `status` (string) - Current status (e.g., "Available", "Sold", "Pending")

**Optional Fields:**
- `stockNo` (string) - Internal stock number
- `condition` (string) - Condition (e.g., "New", "Used")
- `category` (string) - Category classification
- `widthCategory` (string) - Width dimension
- `sizeCategory` (string) - Size dimension
- `description` (string) - Detailed description
- 

**Request:**
```http
POST /api/vehicles/add HTTP/1.1
Host: localhost:7071
Content-Type: application/json

{
  "vin": "1HGBH41JXMN109186",
  "year": 2024,
  "make": "Honda",
  "model": "Civic",
  "typeId": 2,
  "price": 25000.00,
  "status": "Available",
  "stockNo": "HD2024-01",
  "condition": "New"
}
```

**Response (201 Created):**
```json
{
  "success": true,
  "message": "Vehicle added successfully",
  "unitId": 42,
  "vin": "1HGBH41JXMN109186",
  "stockNo": "HD2024-01",
  "responseTimeMs": 245,
  "timestamp": "2025-10-18T14:30:00Z"
}
```

**Response (400 Bad Request - Validation Error):**
```json
{
  "error": true,
  "message": "Validation failed",
  "errors": [
    "VIN is required",
    "Year must be between 1900 and 2027",
    "Price must be a positive number"
  ],
  "statusCode": 400
}
```

**Response (409 Conflict - Duplicate VIN):**
```json
{
  "error": true,
  "message": "VIN '1HGBH41JXMN109186' already exists in inventory",
  "field": "vin",
  "statusCode": 409
}
```

**Response (409 Conflict - Duplicate Stock Number):**
```json
{
  "error": true,
  "message": "Stock Number 'HD2024-01' already exists in inventory",
  "field": "stockNo",
  "statusCode": 409
}
```

---

## 🤖 AI Utilities

### Rewrite Description
**Endpoint:** `POST /api/ai/rewrite`

**Description:** Uses Azure OpenAI to rewrite an inventory description to be clear and professional, without adding features. Returns status stages (`received`, `loading`, `complete`) and includes the built prompt so the frontend can preview/edit.

**Config Required:**
- `openAIEndpoint` – Azure OpenAI endpoint (e.g., https://your-resource.openai.azure.com)
- `openAIkey` – API key for the Azure OpenAI resource
- `openAIDeployment` – Default model deployment name (e.g., `gpt-4o-mini`)

**Request (JSON):**
```json
{
  "description": "Sleeps 6, queen bed, dinette, great for families.",
  "tone": "professional",
  "maxWords": 120,
  "model": "gpt-4o-mini",
  "previewOnly": false
}
```

Fields:
- `description` (required): The original text to rewrite.
- `tone` (optional): `professional|casual|neutral|sales` (default `professional`).
- `maxWords` (optional): Target word count (default `120`).
- `model` (optional): Deployment name to override `openAIDeployment`.
- `previewOnly` (optional): If `true`, returns `promptBuilt` without calling AI.

**Response (success):**
```json
{
  "status": "complete",
  "stages": [
    { "status": "received", "at": "2025-12-28T20:44:00Z" },
    { "status": "loading",  "at": "2025-12-28T20:44:00Z" },
    { "status": "complete", "at": "2025-12-28T20:44:01Z" }
  ],
  "promptBuilt": "Rewrite the following inventory description to be:\n- Clear and professional\n- Easy for a customer to understand\n- No added features\n- Under 120 words\n\nDescription:\nSleeps 6, queen bed, dinette, great for families.",
  "rewrittenText": "Spacious family-friendly unit with a comfortable queen bed and versatile dinette, designed for easy use and a clean, modern experience.",
  "meta": { "tone": "professional", "maxWords": 120, "model": "gpt-4o-mini" }
}
```

**Response (previewOnly):**
```json
{
  "status": "complete",
  "stages": [
    { "status": "received", "at": "..." },
    { "status": "complete", "at": "..." }
  ],
  "promptBuilt": "Rewrite the following inventory description to be:\n- Clear and professional\n- Easy for a customer to understand\n- No added features\n- Under 120 words\n\nDescription:\n...",
  "meta": { "tone": "professional", "maxWords": 120 }
}
```

**Errors:**
```json
{
  "status": "error",
  "stages": [{ "status": "received", "at": "..." }],
  "error": { "message": "Field 'description' is required." }
}
```

**Local Test:**
```bash
curl -X POST "http://localhost:7071/api/ai/rewrite" \
  -H "Content-Type: application/json" \
  -d '{
        "description": "Sleeps 6, queen bed, dinette, great for families.",
        "tone": "professional",
        "maxWords": 120
      }'
```

### 6. Update Existing Inventory
**Endpoint:** `PUT /api/vehicles/{id}`

**Description:** Updates an existing inventory item. Only updates fields that are provided (partial updates supported).

**Use Case:** Admin edits vehicle details or changes status.

**URL Parameters:**
- `id` (required, integer) - The UnitID to update

**All Fields Optional:** Send only the fields you want to update.

**Request (Update price and status only):**
```http
PUT /api/vehicles/1 HTTP/1.1
Host: localhost:7071
Content-Type: application/json

{
  "price": 49950.00,
  "status": "On Sale"
}
```

**Request (Update all fields):**
```http
PUT /api/vehicles/1 HTTP/1.1
Host: localhost:7071
Content-Type: application/json

{
  "vin": "5TJBE51111",
  "year": 2024,
  "make": "Ice Castle Fish House",
  "model": "Extreme III",
  "typeId": 1,
  "price": 52000.00,
  "status": "Sold",
  "stockNo": "IC1111",
  "condition": "Used",
  "category": "RV",
  "widthCategory": "8",
  "sizeCategory": "21",
  "description": "Updated description",
  
}
```

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Vehicle updated successfully",
  "unitId": 1,
  "responseTimeMs": 180,
  "timestamp": "2025-10-18T14:30:00Z"
}
```

**Response (404 Not Found):**
```json
{
  "error": true,
  "message": "Vehicle with UnitID 1 not found",
  "statusCode": 404
}
```

**Response (400 Bad Request - Invalid ID):**
```json
{
  "error": true,
  "message": "Invalid UnitID format. Must be a number.",
  "statusCode": 400
}
```

**Response (409 Conflict - Duplicate VIN):**
```json
{
  "error": true,
  "message": "VIN '5TJBE51111' already exists for another vehicle",
  "field": "vin",
  "statusCode": 409
}
```

**Key Features:**
- ✅ Partial updates (send only changed fields)
- ✅ Prevents duplicate VIN/StockNo across different vehicles
- ✅ Allows updating same vehicle's existing VIN/StockNo
- ✅ Auto-updates `UpdatedAt` timestamp

---

## 🔧 Health & Validation

### 7. Check Database Connection
**Endpoint:** `GET /api/checkdb`

**Description:** Tests database connectivity and returns connection health status.

**Use Case:** Health check endpoint for monitoring/alerting systems.

**Request:**
```http
GET /api/checkdb HTTP/1.1
Host: localhost:7071
```

**Response (200 OK - Connected):**
```json
{
  "connected": true,
  "status": "Success",
  "message": "Database connection is healthy",
  "responseTimeMs": 145,
  "databaseDetails": {
    "server": "tcp:flatt-db-server.database.windows.net,1433",
    "database": "flatt-inv-sql"
  },
  "timestamp": "2025-10-18T14:30:00Z"
}
```

**Response (200 OK - Connection Failed):**
```json
{
  "connected": false,
  "status": "Error",
  "message": "Failed to connect to database",
  "error": "Connection timeout expired",
  "responseTimeMs": 5010,
  "timestamp": "2025-10-18T14:30:00Z"
}
```

**Features:**
- 5-second timeout to avoid hanging
- Returns detailed error messages
- Always returns 200 status (success/failure in body)

---

### 8. Check Vehicle Status
**Endpoint:** `GET /api/checkstatus/{id}`

**Description:** Retrieves the current status of a specific inventory item.

**Use Case:** Quick status lookup for sales staff or website real-time updates.

**URL Parameters:**
- `id` (required, integer) - The UnitID to check

**Request:**
```http
GET /api/checkstatus/1 HTTP/1.1
Host: localhost:7071
```

**Response (200 OK):**
```json
{
  "unitId": 1,
  "status": "Available",
  "responseTimeMs": 78,
  "timestamp": "2025-10-18T14:30:00Z"
}
```

**Response (404 Not Found):**
```json
{
  "error": true,
  "message": "Unit with ID 1 not found",
  "statusCode": 404,
  "timestamp": "2025-10-18T14:30:00Z"
}
```

**Response (400 Bad Request):**
```json
{
  "error": true,
  "message": "UnitID must be a valid number",
  "providedId": "abc",
  "statusCode": 400
}
```

---

### 9. Check VIN Availability
**Endpoint:** `GET /api/checkvin/{vin}`

**Description:** Checks if a VIN already exists in the inventory database.

**Use Case:** Pre-validation before adding new inventory to prevent duplicates.

**URL Parameters:**
- `vin` (required, string) - The VIN to check

**Request:**
```http
GET /api/checkvin/5TJBE51111 HTTP/1.1
Host: localhost:7071
```

**Response (200 OK - VIN Exists):**
```json
{
  "vin": "5TJBE51111",
  "exists": true,
  "message": "VIN already exists in inventory",
  "unitId": 1,
  "stockNo": "IC1111",
  "responseTimeMs": 52
}
```

**Response (200 OK - VIN Available):**
```json
{
  "vin": "1HGBH41JXMN109186",
  "exists": false,
  "message": "VIN is available",
  "unitId": null,
  "stockNo": null,
  "responseTimeMs": 48
}
```

**Response (400 Bad Request - Empty VIN):**
```json
{
  "error": true,
  "message": "VIN parameter is required",
  "statusCode": 400
}
```

**Note:** Always returns 200 OK status. Check the `exists` field to determine availability.

---

## 📊 Dashboard & Reports

### 10. Basic Dashboard Statistics
**Endpoint:** `GET /api/dashboard/stats`

**Description:** Returns basic inventory statistics for admin dashboard overview.

**Use Case:** Homepage dashboard displaying key metrics.

**Request:**
```http
GET /api/dashboard/stats HTTP/1.1
Host: localhost:7071
```

**Response (200 OK):**
```json
{
  "totalItems": 47,
  "totalValue": 1234567.50,
  "availableItems": 32,
  "lastUpdated": "2025-10-18T14:30:00Z",
  "responseTimeMs": 156
}
```

**Metrics Explained:**
- `totalItems` - Total count of all inventory records
- `totalValue` - Sum of all Price values (in dollars)
- `availableItems` - Count of items with Status = "Available"
- `lastUpdated` - Timestamp of the response
- `responseTimeMs` - Query execution time

---

### 11. Advanced Reports Dashboard
**Endpoint:** `GET /api/reports/dashboard`

**Description:** Returns comprehensive inventory statistics broken down by type and make.

**Use Case:** Detailed reports page for inventory analysis and business insights.

**Request:**
```http
GET /api/reports/dashboard HTTP/1.1
Host: localhost:7071
```

**Response (200 OK):**
```json
{
  "totalInventoryValue": 1234567.50,
  "totalVehicles": 25,
  "totalFishHouses": 15,
  "totalTrailers": 7,
  "totalUniqueMakes": 12,
  "pendingSales": 5,
  "lastUpdated": "2025-10-18T14:30:00Z",
  "responseTimeMs": 198
}
```

**Metrics Explained:**
- `totalInventoryValue` - Sum of all Price values (in dollars)
- `totalVehicles` - Count of items with TypeID = 2
- `totalFishHouses` - Count of items with TypeID = 1
- `totalTrailers` - Count of items with TypeID = 3
- `totalUniqueMakes` - Count of distinct manufacturers
- `pendingSales` - Count of items with Status = "Pending"
- `lastUpdated` - Timestamp of the response
- `responseTimeMs` - Query execution time

**Use Cases:**
- Executive reporting dashboards
- Inventory distribution analysis
- Sales pipeline tracking
- Manufacturer diversity metrics

---

## 📦 Data Models

### Units Table Schema
```typescript
{
  UnitID: number;           // Primary Key (auto-generated)
  VIN: string;              // Vehicle Identification Number (unique)
  StockNo?: string;         // Stock number (unique, optional)
  Make: string;             // Manufacturer
  Model: string;            // Model name
  Year: number;             // Year of manufacture
  Condition?: string;       // "New", "Used", etc.
  Description?: string;     // Long text description
  
  Category?: string;        // Category classification
  TypeID: number;           // 1=Fish House, 2=Vehicle, 3=Trailer
  WidthCategory?: string;   // Width dimension
  SizeCategory?: string;    // Size dimension
  Price: decimal;           // Price in dollars
  Status: string;           // "Available", "Sold", "Pending", etc.
  CreatedAt: datetime;      // Auto-generated on insert
  UpdatedAt: datetime;      // Auto-updated on changes
}
```

### TypeID Reference
| TypeID | Description |
|--------|-------------|
| 1      | Fish House  |
| 2      | Vehicle     |
| 3      | Trailer     |

---

## ⚠️ Error Handling

### Standard Error Response Format
```json
{
  "error": true,
  "message": "Human-readable error description",
  "statusCode": 400,
  "timestamp": "2025-10-18T14:30:00Z"
}
```

### HTTP Status Codes
| Code | Description | When Used |
|------|-------------|-----------|
| 200  | OK | Successful GET/PUT requests |
| 201  | Created | Successful POST (new resource created) |
| 400  | Bad Request | Invalid input, validation errors |
| 404  | Not Found | Resource doesn't exist |
| 409  | Conflict | Duplicate VIN/StockNo |
| 500  | Internal Server Error | Database errors, unexpected issues |

### Common Error Scenarios

#### Validation Error (400)
```json
{
  "error": true,
  "message": "Validation failed",
  "errors": [
    "VIN is required",
    "Year must be between 1900 and 2027"
  ],
  "statusCode": 400
}
```

#### Not Found (404)
```json
{
  "error": true,
  "message": "Vehicle with UnitID 999 not found",
  "statusCode": 404
}
```

#### Duplicate Error (409)
```json
{
  "error": true,
  "message": "VIN '5TJBE51111' already exists in inventory",
  "field": "vin",
  "statusCode": 409
}
```

#### Server Error (500)
```json
{
  "error": true,
  "message": "An internal server error occurred",
  "details": "Connection timeout: Unable to connect to database",
  "statusCode": 500
}
```

---

## 🔒 Security Considerations

### Current Configuration (Development)
- ✅ CORS enabled for all origins (`*`)
- ✅ Anonymous authentication level
- ❌ No API key required

### Production Recommendations
```json
{
  "recommendations": [
    "Implement API key authentication",
    "Restrict CORS to specific domains",
    "Add rate limiting",
    "Enable Azure AD authentication",
    "Use Function-level authorization",
    "Implement request validation middleware",
    "Add SQL injection prevention (currently using parameterized queries ✅)"
  ]
}
```

---

## 🧪 Development

### Running Tests
```bash
# Build project
dotnet build

# Run local function host
func start

# Test endpoints with curl
curl http://localhost:7071/api/checkdb
curl http://localhost:7071/api/GrabInventoryAll
```

### Project Structure
```
flatt-functions/
├── AddInventory.cs          # POST /api/vehicles/add
├── UpdateInventory.cs       # PUT /api/vehicles/{id}
├── GetById.cs               # GET /api/vehicles/{id}
├── GrabInventoryAll.cs      # GET /api/GrabInventoryAll
├── GrabInventoryFH.cs       # GET /api/GrabInventoryFH
├── GrabInventoryVH.cs       # GET /api/GrabInventoryVH
├── CheckDb.cs               # GET /api/checkdb
├── CheckStatus.cs           # GET /api/checkstatus/{id}
├── CheckVin.cs              # GET /api/checkvin/{vin}
├── GetDashboardStats.cs     # GET /api/dashboard/stats
├── GetReportsDashboard.cs   # GET /api/reports/dashboard
├── Program.cs               # Function app configuration
├── host.json                # Function host settings
└── local.settings.json      # Local configuration (not in repo)
```

### Configuration Files

**host.json:**
```json
{
  "version": "2.0",
  "logging": {
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true,
        "excludedTypes": "Request"
      }
    }
  },
  "extensions": {
    "http": {
      "routePrefix": "api"
    }
  }
}
```

**local.settings.json (template):**
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SqlConnectionString": "Server=tcp:YOUR_SERVER.database.windows.net,1433;Database=YOUR_DB;User ID=YOUR_USER;Password=YOUR_PASSWORD;Encrypt=True;"
  }
}
```

---

## 📝 Response Time Benchmarks

All endpoints include `responseTimeMs` in their responses for performance monitoring:

| Endpoint | Avg Response Time | Notes |
|----------|------------------|-------|
| `/checkdb` | 150-500ms | First call slower due to connection warmup |
| `/GrabInventoryAll` | 100-300ms | Depends on row count |
| `/vehicles/{id}` | 50-150ms | Single row lookup |
| `/checkvin/{vin}` | 40-100ms | Indexed lookup |
| `/dashboard/stats` | 150-300ms | Aggregation query |
| `/reports/dashboard` | 180-350ms | Complex aggregation |
| `/vehicles/add` | 200-400ms | Includes duplicate checks |
| `/vehicles/{id}` (PUT) | 180-350ms | Includes validation + update |

---

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## 📄 License

This project is proprietary and confidential.

---

## 📞 Support

For questions or issues, contact the development team.

---

## 🎯 Quick Reference

### All Endpoints Summary

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/GrabInventoryAll` | Get all inventory |
| GET | `/api/GrabInventoryFH` | Get fish houses only |
| GET | `/api/GrabInventoryVH` | Get vehicles only |
| GET | `/api/vehicles/{id}` | Get single item by ID |
| POST | `/api/vehicles/add` | Add new inventory |
| PUT | `/api/vehicles/{id}` | Update existing inventory |
| GET | `/api/checkdb` | Database health check |
| GET | `/api/checkstatus/{id}` | Get item status |
| GET | `/api/checkvin/{vin}` | Check VIN availability |
| GET | `/api/dashboard/stats` | Basic dashboard stats |
| GET | `/api/reports/dashboard` | Advanced reports |

---

**Version:** 1.0.0  
**Last Updated:** October 18, 2025  
**Built with:** ❤️ and ☕ by the Flatt team
