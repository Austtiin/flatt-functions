# 🚗 Flatt Functions - Inventory Management API Documentation

## 📋 Overview

This API provides comprehensive inventory management capabilities for vehicles, fish houses, and trailers. All endpoints support CORS and return JSON responses with detailed error handling.

**Base URL:** `http://localhost:7071/api` (Development)

---

## 🔍 Data Models

### Vehicle/Unit Data Structure
```typescript
interface VehicleUnit {
  unitId?: number;          // Primary Key (auto-generated)
  vin: string;             // Vehicle Identification Number (required, unique)
  stockNo?: string;        // Stock number (optional, unique if provided)
  make: string;            // Manufacturer (required)
  model: string;           // Model name (required)
  year: number;            // Year of manufacture (required, 1900-2027)
  condition?: string;      // "New", "Used", etc. (optional)
  description?: string;    // Long text description (optional)
  category?: string;       // Category classification (optional)
  typeId: number;          // Required: 1=Fish House, 2=Vehicle, 3=Trailer
  widthCategory?: string;  // Width dimension (optional)
  sizeCategory?: string;   // Size dimension (optional)
  price: number;           // Price in dollars (required, must be positive)
  status: string;          // Required: "Available", "Sold", "Pending", etc.
  color: string;           // Vehicle color (required)
}
```

---

## 📡 API Endpoints

### 1. Add New Inventory Item
**Endpoint:** `POST /api/vehicles/add`

**Description:** Adds a new vehicle, fish house, or trailer to the inventory.

**Request Body:**
```json
{
  "vin": "1HGBH41JXMN109186",
  "year": 2024,                    // Can be string "2024" or number 2024
  "make": "Honda", 
  "model": "Civic",
  "typeId": 2,                     // Can be string "2" or number 2
  "price": 25000.00,               // Can be string "25000.00" or number 25000.00
  "status": "Available",
  "color": "Red",
  "stockNo": "HD2024-01",          // Optional
  "condition": "New",              // Optional
  "category": "Sedan",             // Optional
  "widthCategory": "Standard",     // Optional
  "sizeCategory": "Compact",       // Optional
  "description": "Low miles"       // Optional
}
```

**Success Response (201 Created):**
```json
{
  "success": true,
  "message": "Vehicle added successfully",
  "unitId": 42,
  "vin": "1HGBH41JXMN109186",
  "stockNo": "HD2024-01",
  "color": "Red",
  "responseTimeMs": 125,
  "timestamp": "2025-10-23T01:30:00.000Z"
}
```

**Error Responses:**

*400 Bad Request - Validation Failed:*
```json
{
  "error": true,
  "message": "Validation failed",
  "errors": [
    "VIN is required",
    "Color is required",
    "Year must be between 1900 and 2027"
  ],
  "statusCode": 400
}
```

*409 Conflict - Duplicate VIN/StockNo:*
```json
{
  "error": true,
  "message": "VIN '1HGBH41JXMN109186' already exists in inventory",
  "field": "vin",
  "statusCode": 409
}
```

---

### 2. Update Existing Inventory Item
**Endpoint:** `PUT /api/vehicles/update/{unitId}`

**Description:** Updates an existing inventory item. Only provided fields will be updated.

**Path Parameters:**
- `unitId` (integer) - The ID of the unit to update

**Request Body (partial update supported):**
```json
{
  "price": 24000.00,              // Can be string or number
  "status": "Sold",
  "color": "Blue",
  "condition": "Used",
  "stockNo": "HD2024-01-USED"     // Optional fields can be updated individually
}
```

**Success Response (200 OK):**
```json
{
  "success": true,
  "message": "Vehicle updated successfully",
  "unitId": 42,
  "responseTimeMs": 89,
  "timestamp": "2025-10-23T01:35:00.000Z"
}
```

**Error Responses:**

*404 Not Found:*
```json
{
  "error": true,
  "message": "Vehicle with UnitID 999 not found",
  "statusCode": 404
}
```

*409 Conflict - Duplicate VIN/StockNo:*
```json
{
  "error": true,
  "message": "VIN '1HGBH41JXMN109186' already exists for another vehicle",
  "field": "vin",
  "statusCode": 409
}
```

---

### 3. Check VIN Availability
**Endpoint:** `GET /api/checkvin/{vin}`

**Description:** Checks if a VIN already exists in the inventory.

**Path Parameters:**
- `vin` (string) - The VIN to check

**Success Response (200 OK):**
```json
{
  "exists": false,
  "message": "VIN is available",
  "unitId": null,
  "stockNo": null,
  "responseTimeMs": 45,
  "timestamp": "2025-10-23T01:40:00.000Z"
}
```

**If VIN exists:**
```json
{
  "exists": true,
  "message": "VIN already exists in inventory",
  "unitId": 42,
  "stockNo": "HD2024-01",
  "responseTimeMs": 45,
  "timestamp": "2025-10-23T01:40:00.000Z"
}
```

---

### 4. Check Unit Status
**Endpoint:** `GET /api/checkstatus/{unitId}`

**Description:** Retrieves the current status of a specific inventory item.

**Path Parameters:**
- `unitId` (integer) - The ID of the unit to check

**Success Response (200 OK):**
```json
{
  "unitId": 42,
  "status": "Available",
  "responseTimeMs": 32,
  "timestamp": "2025-10-23T01:45:00.000Z"
}
```

**Error Response (404 Not Found):**
```json
{
  "error": true,
  "message": "Unit with ID 999 not found",
  "unitId": 999,
  "statusCode": 404
}
```

---

### 5. Get All Inventory
**Endpoint:** `GET /api/GrabInventoryAll`

**Description:** Retrieves all inventory items without filters.

**Success Response (200 OK):**
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
    "color": "White"
  }
]
```

---

### 6. Get Vehicle Inventory Only
**Endpoint:** `GET /api/GrabInventoryVH`

**Description:** Retrieves only vehicles (TypeID = 2) from inventory.

**Success Response:** Same format as Get All Inventory, filtered for vehicles.

---

### 7. Get Fish House Inventory Only
**Endpoint:** `GET /api/GrabInventoryFH`

**Description:** Retrieves only fish houses (TypeID = 1) from inventory.

**Success Response:** Same format as Get All Inventory, filtered for fish houses.

---

### 8. Database Health Check
**Endpoint:** `GET /api/checkdb`

**Description:** Checks database connectivity and health.

**Success Response (200 OK):**
```json
{
  "connected": true,
  "status": "Healthy",
  "message": "Database connection successful",
  "responseTimeMs": 28,
  "databaseDetails": {
    "server": "your-server.database.windows.net",
    "database": "your-database-name"
  },
  "timestamp": "2025-10-23T01:50:00.000Z"
}
```

---

## 🔧 Data Type Flexibility

### Numeric Fields
The API accepts both string and numeric formats for all numeric fields:

✅ **Accepted formats:**
```json
{
  "year": 2024,           // Number
  "year": "2024",         // String
  "typeId": 2,            // Number  
  "typeId": "2",          // String
  "price": 25000.50,      // Number
  "price": "25000.50"     // String
}
```

### Required Fields for Add Operation
- `vin` (string, unique)
- `year` (number, 1900-2027)
- `make` (string)
- `model` (string)
- `typeId` (number: 1, 2, or 3)
- `price` (positive number)
- `status` (string)
- `color` (string)

### Optional Fields
- `stockNo` (string, unique if provided)
- `condition` (string)
- `description` (string)
- `category` (string)
- `widthCategory` (string)
- `sizeCategory` (string)

---

## 🏷️ Type Categories

| TypeID | Category | Description |
|--------|----------|-------------|
| 1 | Fish House | Ice fishing houses and shelters |
| 2 | Vehicle | Cars, trucks, motorcycles, etc. |
| 3 | Trailer | Trailers and towed equipment |

---

## 🚨 Error Handling

### Common HTTP Status Codes
- **200 OK** - Success
- **201 Created** - Resource created successfully
- **400 Bad Request** - Invalid input/validation error
- **404 Not Found** - Resource not found
- **409 Conflict** - Duplicate VIN/StockNo
- **500 Internal Server Error** - Server error

### Error Response Format
```json
{
  "error": true,
  "message": "Description of the error",
  "details": "Additional error details (if available)",
  "statusCode": 400,
  "timestamp": "2025-10-23T01:55:00.000Z"
}
```

---

## 📝 Usage Examples

### Adding a Fish House
```bash
curl -X POST http://localhost:7071/api/vehicles/add \
  -H "Content-Type: application/json" \
  -d '{
    "vin": "ICE123456789",
    "year": "2024",
    "make": "Ice Castle",
    "model": "Extreme III",
    "typeId": "1",
    "price": "54950.00",
    "status": "Available",
    "color": "White",
    "stockNo": "IC2024-001",
    "condition": "New",
    "category": "RV",
    "widthCategory": "8",
    "sizeCategory": "21"
  }'
```

### Updating Vehicle Status
```bash
curl -X PUT http://localhost:7071/api/vehicles/update/42 \
  -H "Content-Type: application/json" \
  -d '{
    "status": "Sold",
    "price": "24000.00"
  }'
```

### Checking VIN Availability
```bash
curl -X GET http://localhost:7071/api/checkvin/1HGBH41JXMN109186
```

---

## 🔒 CORS Support

All endpoints include CORS headers:
- `Access-Control-Allow-Origin: *`
- `Access-Control-Allow-Methods: GET, POST, PUT, OPTIONS`
- `Access-Control-Allow-Headers: Content-Type`

---

## 📊 Performance Notes

- Average response times: 50-150ms
- All responses include `responseTimeMs` field
- Database queries are optimized with proper indexing
- Concurrent requests are supported

---

## 🐛 Troubleshooting

### Common Issues

**Issue:** JSON conversion error for numeric fields
**Solution:** Use the flexible format - both strings and numbers are accepted

**Issue:** VIN/StockNo already exists
**Solution:** Check existing inventory first using the check endpoints

**Issue:** Validation errors
**Solution:** Ensure all required fields are provided with correct data types

### Support Information

For additional support or questions about this API, please refer to the source code or contact the development team.

---

*Last Updated: October 23, 2025*
*API Version: 1.0*