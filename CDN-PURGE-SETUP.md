# Azure Front Door CDN Purge Function Setup Guide

## Overview
This guide explains how to configure and use the `PurgeCdnCache` Azure Function to clear your Azure Front Door CDN cache.

## Function Details
- **Endpoint:** `POST /api/cdn/purge`
- **Function File:** `PurgeCdnCache.cs`
- **Authentication:** Azure Identity (DefaultAzureCredential)
- **Dependencies:** Azure.Identity v1.12.0, Azure.Core v1.41.0

## Required Environment Variables

Add these environment variables to your Azure Function App settings:

```
AZURE_SUBSCRIPTION_ID=your-subscription-id-here
AZURE_RESOURCE_GROUP=your-resource-group-name
AZURE_FD_PROFILE=your-front-door-profile-name
AZURE_FD_ENDPOINT=your-front-door-endpoint-name
```

### How to Find These Values

1. **AZURE_SUBSCRIPTION_ID**
   - Go to Azure Portal → Subscriptions
   - Copy the Subscription ID

2. **AZURE_RESOURCE_GROUP**
   - The resource group containing your Front Door profile

3. **AZURE_FD_PROFILE**
   - Go to Azure Portal → Front Door and CDN profiles
   - Copy your Front Door profile name

4. **AZURE_FD_ENDPOINT**
   - Open your Front Door profile
   - Go to Endpoints
   - Copy the endpoint name (not the full URL)

## Authentication Setup

### For Local Development
1. Install Azure CLI: `winget install Microsoft.AzureCli`
2. Login: `az login`
3. Set subscription: `az account set --subscription "your-subscription-id"`

### For Production (Azure Function App)
1. Enable System-assigned Managed Identity on your Function App:
   - Go to Function App → Identity → System assigned → On
2. Assign permissions to the Managed Identity:
   ```bash
   az role assignment create \
     --assignee <managed-identity-principal-id> \
     --role "CDN Profile Contributor" \
     --scope "/subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.Cdn/profiles/<profile-name>"
   ```

## Request Format

### Request Body Structure
```json
{
  "contentPaths": [
    "/path/to/file1.js",
    "/path/to/file2.css",
    "/images/*",
    "/*"
  ],
  "domains": ["your-domain.com", "www.your-domain.com"]
}
```

### Parameters
- **contentPaths** (required): Array of paths to purge
  - Use `/*` to purge everything
  - Use `/folder/*` to purge a folder
  - Use specific paths like `/js/app.js`
- **domains** (optional): Specific domains to purge from

## Usage Examples

### Purge Everything
```bash
curl -X POST https://your-function-app.azurewebsites.net/api/cdn/purge \
  -H "Content-Type: application/json" \
  -d '{
    "contentPaths": ["/*"]
  }'
```

### Purge Specific Files
```bash
curl -X POST https://your-function-app.azurewebsites.net/api/cdn/purge \
  -H "Content-Type: application/json" \
  -d '{
    "contentPaths": [
      "/js/app.js",
      "/css/styles.css",
      "/images/logo.png"
    ]
  }'
```

### Purge Specific Folder
```bash
curl -X POST https://your-function-app.azurewebsites.net/api/cdn/purge \
  -H "Content-Type: application/json" \
  -d '{
    "contentPaths": ["/js/*", "/css/*"]
  }'
```

### Purge with Specific Domains
```bash
curl -X POST https://your-function-app.azurewebsites.net/api/cdn/purge \
  -H "Content-Type: application/json" \
  -d '{
    "contentPaths": ["/*"],
    "domains": ["your-domain.com", "www.your-domain.com"]
  }'
```

## Response Format

### Success Response
```json
{
  "success": true,
  "message": "CDN purge initiated successfully",
  "purgeId": "12345678-1234-1234-1234-123456789abc",
  "contentPaths": ["/js/*", "/css/*"],
  "responseTimeMs": 1250
}
```

### Error Response
```json
{
  "success": false,
  "error": "Error message details",
  "responseTimeMs": 500
}
```

## Troubleshooting

### Common Issues

1. **Authentication Errors**
   - Ensure Managed Identity is enabled
   - Verify role assignments are correct
   - For local dev, ensure `az login` is completed

2. **Missing Environment Variables**
   - Check all 4 required variables are set
   - Restart Function App after adding variables

3. **Invalid Resource Names**
   - Verify Front Door profile and endpoint names
   - Check resource group name is correct

4. **Permission Denied**
   - Ensure the identity has "CDN Profile Contributor" role
   - Check the scope includes the specific Front Door profile

### Testing the Function

1. **Local Testing**
   ```bash
   # Start the function locally
   func start
   
   # Test the endpoint
   curl -X POST http://localhost:7071/api/cdn/purge \
     -H "Content-Type: application/json" \
     -d '{"contentPaths": ["/test/*"]}'
   ```

2. **Production Testing**
   - Use the production URL
   - Check Application Insights for detailed logs
   - Monitor the purge status in Azure Portal

## Security Notes

- The function uses CORS headers for web access
- Authentication is handled by Azure Identity
- No API keys are stored in code
- All sensitive data is in environment variables

## Monitoring

- Function execution logs are in Application Insights
- CDN purge status can be monitored in Azure Portal → Front Door profile
- Response times are included in all responses

## Additional Resources

- [Azure Front Door Documentation](https://docs.microsoft.com/en-us/azure/frontdoor/)
- [Azure Identity Documentation](https://docs.microsoft.com/en-us/dotnet/api/azure.identity)
- [Azure Functions Documentation](https://docs.microsoft.com/en-us/azure/azure-functions/)