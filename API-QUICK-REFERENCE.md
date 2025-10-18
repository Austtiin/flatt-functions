# 📋 API Quick Reference Guide

Quick lookup for all Flatt Inventory API endpoints.

**Base URL:** `http://localhost:7071/api`

---

## 📊 Inventory Retrieval

| Endpoint | Method | Description | Returns |
|----------|--------|-------------|---------|
| `/GrabInventoryAll` | GET | All inventory (no filters) | Array of all items |
| `/GrabInventoryFH` | GET | Fish Houses only (TypeID=1) | Array of fish houses |
| `/GrabInventoryVH` | GET | Vehicles only (TypeID=2) | Array of vehicles |
| `/vehicles/{id}` | GET | Single item by UnitID | Single item object |

---

## ✏️ Data Management

| Endpoint | Method | Description | Requires | Returns |
|----------|--------|-------------|----------|---------|
| `/vehicles/add` | POST | Add new inventory | JSON body with required fields | UnitID of new item |
| `/vehicles/{id}` | PUT | Update existing inventory | UnitID + JSON body | Success confirmation |

### Required Fields for POST
- `vin`, `year`, `make`, `model`, `typeId`, `price`, `status`

### Optional Fields
- `stockNo`, `condition`, `category`, `widthCategory`, `sizeCategory`, `description`, `thumbnailURL`

---

## 🔧 Health & Validation

| Endpoint | Method | Description | Returns |
|----------|--------|-------------|---------|
| `/checkdb` | GET | Database connection health | Connection status (true/false) |
| `/checkstatus/{id}` | GET | Get status of item | Status string ("Available", "Sold", etc.) |
| `/checkvin/{vin}` | GET | Check if VIN exists | Boolean + UnitID if exists |

---

## 📊 Dashboard & Reports

| Endpoint | Method | Description | Returns |
|----------|--------|-------------|---------|
| `/dashboard/stats` | GET | Basic inventory metrics | Total items, value, available count |
| `/reports/dashboard` | GET | Advanced reports | Breakdown by type, makes, pending sales |

---

## 🔢 TypeID Reference

| TypeID | Type |
|--------|------|
| 1 | Fish House |
| 2 | Vehicle |
| 3 | Trailer |

---

## ⚡ Quick Examples

### Get all inventory
```bash
curl http://localhost:7071/api/GrabInventoryAll
```

### Get single item
```bash
curl http://localhost:7071/api/vehicles/1
```

### Add new vehicle
```bash
curl -X POST http://localhost:7071/api/vehicles/add \
  -H "Content-Type: application/json" \
  -d '{
    "vin": "1HGBH41JXMN109186",
    "year": 2024,
    "make": "Honda",
    "model": "Civic",
    "typeId": 2,
    "price": 25000,
    "status": "Available"
  }'
```

### Update item
```bash
curl -X PUT http://localhost:7071/api/vehicles/1 \
  -H "Content-Type: application/json" \
  -d '{"price": 23500, "status": "On Sale"}'
```

### Check VIN
```bash
curl http://localhost:7071/api/checkvin/5TJBE51111
```

### Check database health
```bash
curl http://localhost:7071/api/checkdb
```

### Get dashboard stats
```bash
curl http://localhost:7071/api/dashboard/stats
```

### Get advanced reports
```bash
curl http://localhost:7071/api/reports/dashboard
```

---

## ✅ HTTP Status Codes

| Code | Meaning | When Used |
|------|---------|-----------|
| 200 | OK | Successful GET/PUT |
| 201 | Created | Successful POST |
| 400 | Bad Request | Validation errors |
| 404 | Not Found | Item doesn't exist |
| 409 | Conflict | Duplicate VIN/StockNo |
| 500 | Server Error | Database issues |

---

## 🚨 Common Errors

### 400 - Validation Error
```json
{
  "error": true,
  "message": "Validation failed",
  "errors": ["VIN is required", "Price must be positive"]
}
```

### 404 - Not Found
```json
{
  "error": true,
  "message": "Vehicle with UnitID 999 not found"
}
```

### 409 - Duplicate
```json
{
  "error": true,
  "message": "VIN '5TJBE51111' already exists in inventory",
  "field": "vin"
}
```

---

**See [README.md](README.md) for complete documentation**
