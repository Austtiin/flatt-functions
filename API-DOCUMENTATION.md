# Flatt Functions - Complete API Reference

**Base URL:** `https://your-function-app.azurewebsites.net/api`

This document provides a comprehensive list of all API endpoints with expected inputs and responses for bot implementation.

---

## Table of Contents
1. [Health & Status Endpoints](#health--status-endpoints)
2. [Inventory Retrieval Endpoints](#inventory-retrieval-endpoints)
3. [Vehicle Management Endpoints](#vehicle-management-endpoints)
4. [Validation Endpoints](#validation-endpoints)
5. [Dashboard & Reports Endpoints](#dashboard--reports-endpoints)
6. [CDN Management Endpoints](#cdn-management-endpoints)
7. [Reference Data Endpoints](#reference-data-endpoints)
8. [Unit Features Endpoints](#unit-features-endpoints)

---

## Health & Status Endpoints

### 1. Check Database Connection
**Purpose:** Health check to verify database connectivity

**Endpoint:** `GET /checkdb`

**Input:** None

**Response:**
```json
{
  "connected": true,
  "status": "Healthy",
  "message": "Database connection successful",
  "responseTimeMs": 145,
  "databaseDetails": {
    "server": "tcp:flatt-db-server.database.windows.net,1433",
    "database": "flatt-inv-sql",
    "connectionTimeout": 30
  },
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response:**
```json
{
  "connected": false,
  "status": "Error",
  "message": "Database connection string not configured",
  "responseTimeMs": 10,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

---

## Inventory Retrieval Endpoints

### 2. Get All Inventory (All Types)
**Purpose:** Retrieve all vehicles, fish houses, and trailers from inventory

**Endpoint:** `GET /GrabInventoryAll`

**Query Parameters:**
- `table` (optional): Table name (default: "Units")
- `format` (optional): Response format (default: "json")
- `schema` (optional): Include schema info (default: false)

**Input:** None

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "unitId": 1,
      "stockNo": "VH001",
      "typeId": 2,
      "year": 2020,
      "make": "Ford",
      "model": "F-150",
      "vin": "1FTFW1E50LFA12345",
      "price": 35000.00,
      "status": "available",
      "mileage": 45000,
      "color": "Blue",
      "description": "Great condition truck",
      "createdAt": "2024-01-15T08:00:00Z",
      "updatedAt": "2024-10-20T10:30:00Z"
    }
  ],
  "count": 1,
  "responseTimeMs": 234,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

### 3. Get Fish House Inventory
**Purpose:** Retrieve only fish houses (TypeID = 1)

**Endpoint:** `GET /GrabInventoryFH`

**Query Parameters:** Same as GrabInventoryAll

**Response:** Same structure as GrabInventoryAll, filtered for fish houses only

### 4. Get Vehicle Inventory
**Purpose:** Retrieve only vehicles (TypeID = 2)

**Endpoint:** `GET /GrabInventoryVH`

**Query Parameters:** Same as GrabInventoryAll

**Response:** Same structure as GrabInventoryAll, filtered for vehicles only

### 5. Get Vehicle By ID
**Purpose:** Retrieve a specific vehicle by UnitID

**Endpoint:** `GET /vehicles/{id}`

**Path Parameters:**
- `id` (required): UnitID of the vehicle (integer)

**Input:** None

**Response:**
```json
{
  "unitId": 1,
  "stockNo": "VH001",
  "typeId": 2,
  "year": 2020,
  "make": "Ford",
  "model": "F-150",
  "vin": "1FTFW1E50LFA12345",
  "price": 35000.00,
  "status": "available",
  "mileage": 45000,
  "color": "Blue",
  "description": "Great condition truck",
  "engine": "V8 5.0L",
  "transmission": "Automatic",
  "fuelType": "Gasoline",
  "drivetrain": "4WD",
  "bodyStyle": "Pickup Truck",
  "doors": 4,
  "seats": 5,
  "mpg": "18/24",
  "createdAt": "2024-01-15T08:00:00Z",
  "updatedAt": "2024-10-20T10:30:00Z",
  "responseTimeMs": 87,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response (Not Found):**
```json
{
  "error": true,
  "message": "Vehicle with ID 999 not found",
  "vehicleId": 999,
  "statusCode": 404,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response (Invalid ID):**
```json
{
  "error": true,
  "message": "Vehicle ID must be a valid number",
  "providedId": "abc",
  "statusCode": 400,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

---

## Vehicle Management Endpoints

### 6. Add New Vehicle
**Purpose:** Add a new vehicle to inventory

**Endpoint:** `POST /vehicles/add`

**Request Body:**
```json
{
  "stockNo": "VH123",
  "typeId": 2,
  "year": 2023,
  "make": "Toyota",
  "model": "Camry",
  "vin": "4T1B11HK5PU123456",
  "price": 28000.00,
  "msrp": 31000.00,
  "status": "available",
  "mileage": 15000,
  "color": "Silver",
  "description": "Excellent condition sedan",
  "engine": "4-Cylinder 2.5L",
  "transmission": "Automatic",
  "fuelType": "Gasoline",
  "drivetrain": "FWD",
  "bodyStyle": "Sedan",
  "doors": 4,
  "seats": 5,
  "mpg": "28/39"
}
```

**Required Fields:**
- `stockNo` (string)
- `typeId` (integer): 1=Fish House, 2=Vehicle, 3=Trailer
- `year` (integer)
- `make` (string)
- `model` (string)
- `vin` (string)
- `price` (decimal)

**Success Response:**
```json
{
  "success": true,
  "message": "Vehicle added successfully",
  "unitId": 145,
  "stockNo": "VH123",
  "vin": "4T1B11HK5PU123456",
  "responseTimeMs": 198,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response (Validation):**
```json
{
  "error": true,
  "message": "Validation failed",
  "validationErrors": [
    "StockNo is required",
    "VIN is required",
    "Price must be greater than 0",
    "MSRP must be 0 or greater"
  ],
  "statusCode": 400
}
```

**Error Response (Duplicate VIN):**
```json
{
  "error": true,
  "message": "VIN already exists in inventory",
  "vin": "4T1B11HK5PU123456",
  "existingUnitId": 100,
  "statusCode": 409
}
```

### 7. Update Vehicle
**Purpose:** Update an existing vehicle in inventory

**Endpoint:** `PUT /vehicles/{id}`

**Path Parameters:**
- `id` (required): UnitID of the vehicle (integer)

**Request Body:** (All fields optional, only send fields to update)
```json
{
  "price": 27000.00,
  "msrp": 30500.00,
  "mileage": 16500,
  "status": "pending",
  "description": "Updated description"
}
```

**Success Response:**
```json
{
  "success": true,
  "message": "Vehicle updated successfully",
  "unitId": 145,
  "updatedFields": ["price", "mileage", "status", "description"],
  "fieldsCount": 4,
  "responseTimeMs": 156,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response (Not Found):**
```json
{
  "error": true,
  "message": "Vehicle with UnitID 999 not found",
  "statusCode": 404
}
```

### 8. Set Vehicle Status
**Purpose:** Update the status of a specific vehicle

**Endpoint:** `POST /SetStatus/{id}`

**Path Parameters:**
- `id` (required): UnitID of the vehicle (integer)

**Request Body:**
```json
{
  "status": "sold"
}
```

**Valid Status Values:**
- `available`
- `pending`
- `sold`
- `reserved`
- `maintenance`

**Success Response:**
```json
{
  "success": true,
  "message": "Status updated successfully",
  "unitId": 145,
  "oldStatus": "available",
  "newStatus": "sold",
  "updatedAt": "2025-10-20T10:30:00Z",
  "responseTimeMs": 89,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response:**
```json
{
  "error": true,
  "message": "Invalid status value. Must be one of: available, pending, sold, reserved, maintenance",
  "providedStatus": "invalid_status",
  "statusCode": 400
}
```

---

## Validation Endpoints

### 9. Check VIN Existence
**Purpose:** Check if a VIN already exists in the inventory

**Endpoint:** `GET /checkvin/{vin}`

**Path Parameters:**
- `vin` (required): Vehicle Identification Number (string)

**Input:** None

**Response (VIN Exists):**
```json
{
  "vin": "1FTFW1E50LFA12345",
  "exists": true,
  "message": "VIN already exists in inventory",
  "unitId": 45,
  "stockNo": "VH001",
  "responseTimeMs": 56,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Response (VIN Available):**
```json
{
  "vin": "NEW12345678901234",
  "exists": false,
  "message": "VIN is available",
  "unitId": null,
  "stockNo": null,
  "responseTimeMs": 43,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

### 10. Check Vehicle Status
**Purpose:** Get the current status of a vehicle

**Endpoint:** `GET /checkstatus/{id}`

**Path Parameters:**
- `id` (required): UnitID of the vehicle (integer)

**Input:** None

**Response:**
```json
{
  "unitId": 45,
  "stockNo": "VH001",
  "status": "available",
  "make": "Ford",
  "model": "F-150",
  "year": 2020,
  "price": 35000.00,
  "lastUpdated": "2024-10-15T14:30:00Z",
  "responseTimeMs": 67,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response (Not Found):**
```json
{
  "error": true,
  "message": "Unit with ID 999 not found",
  "unitId": 999,
  "statusCode": 404
}
```

---

## Dashboard & Reports Endpoints

### 11. Get Dashboard Statistics
**Purpose:** Retrieve aggregate statistics for the inventory dashboard

**Endpoint:** `GET /dashboard/stats`

**Input:** None

**Response:**
```json
{
  "totalItems": 156,
  "totalValue": 4250000.00,
  "availableItems": 142,
  "pendingItems": 8,
  "soldItems": 6,
  "averagePrice": 27243.59,
  "totalVehicles": 89,
  "totalFishHouses": 45,
  "totalTrailers": 22,
  "recentlyAddedCount": 12,
  "lowInventoryAlert": false,
  "responseTimeMs": 178,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

### 12. Get Reports Dashboard
**Purpose:** Retrieve comprehensive reporting statistics

**Endpoint:** `GET /reports/dashboard`

**Input:** None

**Response:**
```json
{
  "totalInventoryValue": 4250000.00,
  "totalVehicles": 89,
  "totalFishHouses": 45,
  "totalTrailers": 22,
  "totalUniqueMakes": 24,
  "pendingSales": 8,
  "totalItems": 156,
  "averageVehiclePrice": 47752.81,
  "averageFishHousePrice": 18500.00,
  "topMakes": [
    {
      "make": "Ford",
      "count": 23,
      "totalValue": 985000.00
    },
    {
      "make": "Chevrolet",
      "count": 18,
      "totalValue": 756000.00
    }
  ],
  "salesByStatus": {
    "available": 142,
    "pending": 8,
    "sold": 6
  },
  "monthlyStats": {
    "addedThisMonth": 12,
    "soldThisMonth": 6,
    "pendingThisMonth": 8
  },
  "responseTimeMs": 234,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

---

## CDN Management Endpoints

### 13. Purge CDN Cache
**Purpose:** Purge Azure Front Door CDN cache for specified content paths

**Endpoint:** `POST /cdn/purge`

**Request Body:**
```json
{
  "contentPaths": [
    "/*",
    "/images/*",
    "/vehicles/*",
    "/css/style.css"
  ],
  "domains": [
    "www.example.com"
  ]
}
```

**Required Fields:**
- `contentPaths` (array of strings): Paths to purge from cache

**Optional Fields:**
- `domains` (array of strings): Specific domains to purge (optional)

**Success Response:**
```json
{
  "success": true,
  "message": "CDN cache purge initiated successfully",
  "purgeId": "abc123-def456-ghi789",
  "contentPaths": [
    "/*",
    "/images/*",
    "/vehicles/*",
    "/css/style.css"
  ],
  "estimatedCompletionTime": "2-5 minutes",
  "responseTimeMs": 1234,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

**Error Response (Missing Configuration):**
```json
{
  "error": true,
  "message": "Azure Front Door configuration is incomplete",
  "missingVariables": [
    "AZURE_SUBSCRIPTION_ID",
    "AZURE_RESOURCE_GROUP"
  ],
  "requiredVariables": [
    "AZURE_SUBSCRIPTION_ID",
    "AZURE_RESOURCE_GROUP",
    "AZURE_FD_PROFILE",
    "AZURE_FD_ENDPOINT"
  ],
  "statusCode": 500
}
```

**Error Response (Invalid Request):**
```json
{
  "error": true,
  "message": "Request body is required with 'contentPaths' field",
  "expectedFormat": {
    "contentPaths": ["/*", "/images/*", "/css/style.css"],
    "domains": ["www.example.com"]
  },
  "statusCode": 400
}
```

---

## Vehicle Deletion Endpoint

### 14. Delete Vehicle
**Purpose:** Permanently delete a vehicle from inventory by UnitID

**Endpoint:**
- `DELETE /vehicles/delete/{id}`
- Also available for convenience/testing: `GET /vehicles/delete/{id}` (performs the same delete)

**Path Parameters:**
- `id` (required): UnitID of the vehicle (integer)

**Response (Success):**
```json
{
  "success": true,
  "message": "Vehicle deleted successfully",
  "unitId": 145,
  "responseTimeMs": 42,
  "timestamp": "2025-10-24T10:30:00Z"
}
```

**Error Response (Not Found):**
```json
{
  "error": true,
  "message": "Vehicle with UnitID 999 not found",
  "statusCode": 404
}
```

**Error Response (Invalid ID):**
```json
{
  "error": true,
  "message": "Invalid UnitID format. Must be a number.",
  "statusCode": 400
}
```

> Note: This operation is destructive. Consider purging related CDN entries after deletion if the item was publicly listed.

---

## Reference Data Endpoints

### 15. Get Features
**Purpose:** Retrieve all available features from the `dbo.FeatureList` lookup table

**Endpoint:** `GET /features`

**Input:** None

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "featureId": 1,
      "name": "Heated Seats",
      "category": "Comfort",
      "isActive": true
    },
    {
      "featureId": 2,
      "name": "Bluetooth",
      "category": "Audio",
      "isActive": true
    }
  ],
  "count": 2,
  "responseTimeMs": 42,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

Notes:
- The response dynamically includes all columns from `dbo.FeatureList`.
- CORS headers are included as with other endpoints.


## Common Response Patterns

### CORS Headers
All endpoints include the following CORS headers:
```
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, POST, PUT, OPTIONS
Access-Control-Allow-Headers: Content-Type
```

### Standard Error Response Structure
```json
{
  "error": true,
  "message": "Human-readable error message",
  "statusCode": 400,
  "timestamp": "2025-10-20T10:30:00Z"
}
```

### HTTP Status Codes
- `200 OK` - Successful request
- `400 Bad Request` - Invalid input or validation error
- `404 Not Found` - Resource not found
- `409 Conflict` - Duplicate resource (e.g., VIN already exists)
- `500 Internal Server Error` - Server-side error

---

## Unit Features Endpoints

### 16. Get Features For Unit
**Purpose:** Retrieve all feature mappings attached to the specified unit

**Endpoint:** `GET /units/{id}/features`

**Path Parameters:**
- `id` (required): UnitID (integer)

**Input:** None

**Response:**
```json
[
  {
    "unitId": 145,
    "featureId": 2,
    "name": "Bluetooth",
    "category": "Audio",
    "isActive": true
  }
]
```

Notes:
- The response dynamically includes all columns from `dbo.UnitFeatures` (and any joins if added in the future). Currently it returns the raw mapping rows from `UnitFeatures`.

**Error Response (Invalid ID):**
```json
{
  "error": true,
  "message": "Invalid UnitID format. Must be a positive number.",
  "statusCode": 400
}
```

### 17. Get All Unit Features
**Purpose:** Retrieve the entire `dbo.UnitFeatures` table

**Endpoint:** `GET /unit-features`

**Input:** None

**Response:**
```json
[
  {
    "unitId": 145,
    "featureId": 1
  },
  {
    "unitId": 146,
    "featureId": 3
  }
]
```

Notes:
- The response dynamically includes all columns from `dbo.UnitFeatures`.
- CORS headers are included as with other endpoints.

### 18. Update Unit Features (Replace)
**Purpose:** Replace all features for a given unit. Deletes any existing rows in `dbo.UnitFeatures` for the unit and inserts the provided FeatureIDs.

**Endpoint:** `PUT /units/{id}/features`

**Path Parameters:**
- `id` (required): UnitID (integer)

**Request Body:**
You can send either an object with `featureIds` or a raw array.

Option A (recommended):
```json
{
  "featureIds": [1, 4, 7]
}
```

Option B (raw array):
```json
[1, 4, 7]
```

Behavior:
- If the list is empty (`[]`), all existing features for the unit are removed.
- Duplicate or non-positive FeatureIDs are ignored (the list is de-duplicated and filtered to positive integers).
- Operation runs in a single transaction: delete existing then insert new rows.

**Success Response:**
```json
{
  "success": true,
  "message": "Unit features updated successfully",
  "unitId": 145,
  "deletedCount": 3,
  "insertedCount": 3,
  "featureIds": [1, 4, 7]
}
```

**Error Response (Invalid ID):**
```json
{
  "error": true,
  "message": "Invalid UnitID format. Must be a positive number.",
  "statusCode": 400
}
```

**Error Response (Not Found):**
```json
{
  "error": true,
  "message": "Vehicle with UnitID 999 not found",
  "statusCode": 404
}
```


## Bot Implementation Examples

### Example 1: Add a New Vehicle via Bot
```javascript
// Bot receives: "Add a 2023 Toyota Camry, VIN 4T1B11HK5PU123456, price $28,000"

const vehicleData = {
  stockNo: generateStockNo(), // Generate or prompt user
  typeId: 2, // Vehicle
  year: 2023,
  make: "Toyota",
  model: "Camry",
  vin: "4T1B11HK5PU123456",
  price: 28000.00,
  status: "available",
  color: "Silver", // Extract from conversation or prompt
  mileage: 0 // New vehicle
};

const response = await fetch(`${BASE_URL}/vehicles/add`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(vehicleData)
});

const result = await response.json();
// Bot responds: "✅ Vehicle added successfully! UnitID: 145, Stock#: VH123"
```

### Example 2: Check Inventory Status via Bot
```javascript
// Bot receives: "What's the status of unit 45?"

const response = await fetch(`${BASE_URL}/checkstatus/45`);
const result = await response.json();

// Bot responds: "🚗 Unit #45 (VH001) - 2020 Ford F-150
// Status: Available | Price: $35,000 | Last Updated: Oct 15, 2024"
```

### Example 3: Get Dashboard Summary via Bot
```javascript
// Bot receives: "Show me inventory summary"

const response = await fetch(`${BASE_URL}/dashboard/stats`);
const result = await response.json();

// Bot responds: "📊 Inventory Summary:
// • Total Items: 156 | Value: $4,250,000
// • Available: 142 | Pending: 8 | Sold: 6
// • Vehicles: 89 | Fish Houses: 45 | Trailers: 22"
```

### Example 4: Update Vehicle Price via Bot
```javascript
// Bot receives: "Update price of unit 145 to $27,000"

const updateData = {
  price: 27000.00
};

const response = await fetch(`${BASE_URL}/vehicles/145`, {
  method: 'PUT',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(updateData)
});

const result = await response.json();
// Bot responds: "✅ Price updated successfully for Unit #145!"
```

### Example 5: Search for Vehicle by VIN via Bot
```javascript
// Bot receives: "Check if VIN 1FTFW1E50LFA12345 exists"

const vin = "1FTFW1E50LFA12345";
const response = await fetch(`${BASE_URL}/checkvin/${vin}`);
const result = await response.json();

if (result.exists) {
  // Bot responds: "⚠️ VIN already exists! Unit #45, Stock# VH001"
} else {
  // Bot responds: "✅ VIN is available for use!"
}
```

---

## Data Type Reference

### TypeID Values
- `1` - Fish House
- `2` - Vehicle
- `3` - Trailer

### Status Values
- `available` - Item is available for sale
- `pending` - Sale is pending/in progress
- `sold` - Item has been sold
- `reserved` - Item is reserved for a customer
- `maintenance` - Item is in maintenance/repair

### Vehicle Properties Data Types
```typescript
interface Vehicle {
  unitId: number;
  stockNo: string;
  typeId: 1 | 2 | 3;
  year: number;
  make: string;
  model: string;
  vin: string;
  price: number; // decimal
  msrp?: number; // decimal (optional)
  status: 'available' | 'pending' | 'sold' | 'reserved' | 'maintenance';
  mileage?: number;
  color?: string;
  description?: string;
  engine?: string;
  transmission?: string;
  fuelType?: string;
  drivetrain?: string;
  bodyStyle?: string;
  doors?: number;
  seats?: number;
  mpg?: string;
  createdAt: string; // ISO 8601 datetime
  updatedAt: string; // ISO 8601 datetime
}
```

---

## Rate Limiting & Best Practices

1. **Response Times:** Most endpoints respond within 50-250ms
2. **Batch Operations:** For multiple items, make individual calls with small delays
3. **Error Handling:** Always check for `error: true` in responses
4. **VIN Validation:** Always check VIN before adding new vehicles
5. **Status Updates:** Use dedicated `/SetStatus` endpoint for status changes
6. **Cache Management:** Purge CDN after significant inventory updates

---

## Environment Configuration

Required environment variables for full functionality:
- `SqlConnectionString` - Database connection string
- `AZURE_SUBSCRIPTION_ID` - Azure subscription (CDN purge)
- `AZURE_RESOURCE_GROUP` - Resource group (CDN purge)
- `AZURE_FD_PROFILE` - Front Door profile (CDN purge)
- `AZURE_FD_ENDPOINT` - Front Door endpoint (CDN purge)

---

## Support & Contact

For API issues, database connectivity problems, or feature requests, please create an issue in the GitHub repository.

**Last Updated:** October 20, 2025
**Version:** 1.0.0
**Maintained By:** Flatt Functions Team
