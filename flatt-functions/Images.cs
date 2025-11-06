using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;

namespace flatt_functions
{
    public class Images
    {
        private readonly ILogger<Images> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public Images(ILogger<Images> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _connectionString =
                configuration["SqlConnectionString"] ??
                configuration.GetConnectionString("SqlConnectionString") ??
                configuration["ConnectionStrings:SqlConnectionString"]
                ?? throw new InvalidOperationException("SqlConnectionString not set in configuration.");
        }

        // GET /units/{id:int}/images -> list image names and urls
        [Function("ListUnitImages")]
        public async Task<HttpResponseData> List(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "units/{id:int}/images")] HttpRequestData req,
            int id)
        {
            var res = req.CreateResponse();
            AddCors(res);
            try
            {
                var vin = await GetVinForUnit(id);
                if (string.IsNullOrWhiteSpace(vin))
                {
                    return await NotFound(res, $"UnitID {id} not found");
                }

                var container = ResolveContainerClient();
                if (container == null)
                {
                    return await Error(res, "Blob storage not configured");
                }

                var basePrefix = GetBlobPathPrefix();
                var prefix = basePrefix + vin + "/";

                var items = new System.Collections.Generic.List<object>();
                await foreach (var blob in container.GetBlobsAsync(prefix: prefix))
                {
                    var fullName = blob.Name; // e.g., invpics/units/VIN/3.jpg
                    var fileName = fullName.Substring(prefix.Length);
                    // Skip hidden/placeholder blobs like .init or any name starting with a dot
                    if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith(".", StringComparison.Ordinal))
                        continue;
                    var url = BuildPublicUrl(container, fullName, fileName, vin);
                    items.Add(new { name = fileName, url });
                }

                // Sort by numeric filename if possible
                var sorted = items
                    .Select(x => (dynamic)x)
                    .OrderBy(x => TryExtractLeadingInt(x.name))
                    .ThenBy(x => x.name)
                    .ToArray();

                res.StatusCode = HttpStatusCode.OK;
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                // Cache image listings for 30 days (rarely changes)
                res.Headers.Add("Cache-Control", "public, max-age=2592000, s-maxage=2592000");
                await res.WriteStringAsync(JsonSerializer.Serialize(sorted, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "List images failed for UnitID {id}", id);
                return await Error(res, ex.Message);
            }
        }

        // GET /units/{id:int}/images/{name} -> redirect to blob (or stream)
        [Function("GetUnitImage")]
        public async Task<HttpResponseData> Get(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "units/{id:int}/images/{name}")] HttpRequestData req,
            int id,
            string name)
        {
            var res = req.CreateResponse();
            AddCors(res);
            try
            {
                var vin = await GetVinForUnit(id);
                if (string.IsNullOrWhiteSpace(vin)) return await NotFound(res, $"UnitID {id} not found");

                var container = ResolveContainerClient();
                if (container == null) return await NotFound(res, "Storage not configured");

                var basePrefix = GetBlobPathPrefix();
                var blobPath = basePrefix + vin + "/" + name;
                var blob = container.GetBlobClient(blobPath);
                if (!await blob.ExistsAsync())
                {
                    return await NotFound(res, $"Image '{name}' not found");
                }

                // Prefer redirect to public URL when base URL is configured
                var redirectUrl = BuildPublicUrl(container, blobPath, name, vin);
                res.StatusCode = HttpStatusCode.Redirect;
                res.Headers.Add("Location", redirectUrl);
                // Allow clients and CDNs to cache the redirect/target for 30 days
                res.Headers.Add("Cache-Control", "public, max-age=2592000, s-maxage=2592000");
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get image failed for UnitID {id}, name {name}", id, name);
                return await Error(res, ex.Message);
            }
        }

        // POST /units/{id:int}/images -> upload single image; name assigned sequentially
        [Function("UploadUnitImage")]
        public async Task<HttpResponseData> Upload(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{id:int}/images")] HttpRequestData req,
            int id)
        {
            var res = req.CreateResponse();
            AddCors(res);
            try
            {
                var vin = await GetVinForUnit(id);
                if (string.IsNullOrWhiteSpace(vin)) return await NotFound(res, $"UnitID {id} not found");

                var container = ResolveContainerClient();
                if (container == null) return await Error(res, "Blob storage not configured");

                // read request body into memory
                using var incoming = new MemoryStream();
                await req.Body.CopyToAsync(incoming);
                incoming.Position = 0;

                // validate and decode as image; only allow image formats
                using var webpStream = new MemoryStream();
                try
                {
                    incoming.Position = 0;
                    using var magick = new MagickImage(incoming);
                    // Convert to WebP with quality 80
                    magick.Quality = 80;
                    magick.Write(webpStream, MagickFormat.WebP);
                }
                catch
                {
                    return await BadRequest(res, "Only image uploads are accepted (jpg, jpeg, png, webp, gif). The payload was not recognized as an image.");
                }
                webpStream.Position = 0;

                // find next index
                var basePrefix = GetBlobPathPrefix();
                var prefix = basePrefix + vin + "/";
                int maxIndex = 0;
                await foreach (var bitem in container.GetBlobsAsync(prefix: prefix))
                {
                    var fileName = bitem.Name.Substring(prefix.Length);
                    var n = TryExtractLeadingInt(fileName);
                    if (n.HasValue && n.Value > maxIndex) maxIndex = n.Value;
                }
                var nextIndex = maxIndex + 1;
                // force .webp extension for stored image
                var newName = nextIndex.ToString() + ".webp";
                var blobPath = prefix + newName;

                // upload converted webp
                var blob = container.GetBlobClient(blobPath);
                var headers = new Azure.Storage.Blobs.Models.BlobHttpHeaders
                {
                    ContentType = "image/webp"
                };
                await blob.UploadAsync(webpStream, httpHeaders: headers);

                var url = BuildPublicUrl(container, blobPath, newName, vin);
                res.StatusCode = HttpStatusCode.Created;
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(JsonSerializer.Serialize(new { name = newName, url }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return res;
            }
            catch (Azure.RequestFailedException rfe) when (rfe.Status == 409)
            {
                res.StatusCode = HttpStatusCode.Conflict;
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message = "Conflict uploading image (already exists). Retry." }));
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload image failed for UnitID {id}", id);
                return await Error(res, ex.Message);
            }
        }

        // DELETE /units/{id:int}/images/{name}
        [Function("DeleteUnitImage")]
        public async Task<HttpResponseData> Delete(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "units/{id:int}/images/{name}")] HttpRequestData req,
            int id,
            string name)
        {
            var res = req.CreateResponse();
            AddCors(res);
            try
            {
                if (!IsValidImageFileName(name))
                {
                    return await BadRequest(res, "Invalid image name. Use e.g., 1.jpg, 2.png.");
                }
                var vin = await GetVinForUnit(id);
                if (string.IsNullOrWhiteSpace(vin)) return await NotFound(res, $"UnitID {id} not found");

                var container = ResolveContainerClient();
                if (container == null) return await Error(res, "Blob storage not configured");

                var basePrefix = GetBlobPathPrefix();
                var blobPath = basePrefix + vin + "/" + name;
                var ok = await container.DeleteBlobIfExistsAsync(blobPath);

                res.StatusCode = ok.Value ? HttpStatusCode.NoContent : HttpStatusCode.NotFound;
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete image failed for UnitID {id}, name {name}", id, name);
                return await Error(res, ex.Message);
            }
        }

        // PUT /units/{id:int}/images/{oldName}/rename/{newName}
        [Function("RenameUnitImage")]
        public async Task<HttpResponseData> Rename(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "units/{id:int}/images/{oldName}/rename/{newName}")] HttpRequestData req,
            int id,
            string oldName,
            string newName)
        {
            var res = req.CreateResponse();
            AddCors(res);
            try
            {
                if (!IsValidImageFileName(oldName) || !IsValidImageFileName(newName))
                {
                    return await BadRequest(res, "Invalid image file names. Use numeric names like 1.jpg");
                }
                var vin = await GetVinForUnit(id);
                if (string.IsNullOrWhiteSpace(vin)) return await NotFound(res, $"UnitID {id} not found");
                var container = ResolveContainerClient();
                if (container == null) return await Error(res, "Blob storage not configured");

                var basePrefix = GetBlobPathPrefix();
                var unitPrefix = basePrefix + vin + "/";

                // No-op: identical names (case-insensitive). Verify the blob exists, then return OK without changes.
                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    var sameClient = container.GetBlobClient(unitPrefix + oldName);
                    if (!await sameClient.ExistsAsync())
                        return await NotFound(res, $"Image '{oldName}' not found");

                    res.StatusCode = HttpStatusCode.OK;
                    res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await res.WriteStringAsync(JsonSerializer.Serialize(new { oldName, newName, moved = false }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                    return res;
                }

                // Parse indices
                var oldIdx = TryExtractLeadingInt(oldName);
                var newIdx = TryExtractLeadingInt(newName);
                if (!oldIdx.HasValue || !newIdx.HasValue)
                {
                    return await BadRequest(res, "File names must start with a numeric position, e.g., 1.jpg");
                }

                // Short-circuit: same index but different extension => simple rename preserving index
                if (oldIdx.Value == newIdx.Value)
                {
                    var srcPathSame = unitPrefix + oldName;
                    var dstPathSame = unitPrefix + newName;
                    var srcSame = container.GetBlobClient(srcPathSame);
                    if (!await srcSame.ExistsAsync())
                        return await NotFound(res, $"Image '{oldName}' not found");
                    if (await container.GetBlobClient(dstPathSame).ExistsAsync())
                        return await Conflict(res, $"Destination '{newName}' already exists");

                    // Preserve content type on rename
                    var props = await srcSame.GetPropertiesAsync();
                    var headers = new BlobHttpHeaders { ContentType = props.Value.ContentType };
                    using var msSame = new MemoryStream();
                    await srcSame.DownloadToAsync(msSame);
                    msSame.Position = 0;
                    await container.GetBlobClient(dstPathSame).UploadAsync(msSame, httpHeaders: headers);
                    await srcSame.DeleteIfExistsAsync();

                    res.StatusCode = HttpStatusCode.OK;
                    res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await res.WriteStringAsync(JsonSerializer.Serialize(new { oldName, newName, moved = false }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                    return res;
                }

                // Enumerate all blobs in the unit folder once
                var blobNames = new System.Collections.Generic.List<string>();
                await foreach (var b in container.GetBlobsAsync(prefix: unitPrefix))
                {
                    var fileName = b.Name.Substring(unitPrefix.Length);
                    blobNames.Add(fileName);
                }

                // Helper to find the actual name (with extension) for an index
                string? FindByIndex(int idx)
                {
                    var pat = new Regex($"^{idx}\\.(jpg|jpeg|png|webp|gif)$", RegexOptions.IgnoreCase);
                    return blobNames.FirstOrDefault(n => pat.IsMatch(n));
                }

                var sourceActualName = FindByIndex(oldIdx.Value);
                if (string.IsNullOrEmpty(sourceActualName))
                    return await NotFound(res, $"Image '{oldName}' not found");

                // Build move plan with two phases (temp then final) to avoid overwrite conflicts
                var plan = new System.Collections.Generic.List<(string src, string temp, string final)>();
                var tempPrefix = unitPrefix + "__tmp__/" + Guid.NewGuid().ToString("N") + "/";

                if (oldIdx.Value < newIdx.Value)
                {
                    // Move down: shift [oldIdx+1..newIdx] down by -1
                    for (int i = oldIdx.Value + 1; i <= newIdx.Value; i++)
                    {
                        var nameAtI = FindByIndex(i);
                        if (string.IsNullOrEmpty(nameAtI)) continue; // hole
                        var finalName = (i - 1).ToString() + nameAtI.Substring(nameAtI.IndexOf('.'));
                        plan.Add((unitPrefix + nameAtI, tempPrefix + nameAtI, unitPrefix + finalName));
                    }
                    // Finally move source to newIdx (keep its extension)
                    var finalSrcName = newIdx.Value.ToString() + sourceActualName.Substring(sourceActualName.IndexOf('.'));
                    plan.Add((unitPrefix + sourceActualName, tempPrefix + sourceActualName, unitPrefix + finalSrcName));
                }
                else
                {
                    // Move up: shift [newIdx..oldIdx-1] up by +1
                    for (int i = oldIdx.Value - 1; i >= newIdx.Value; i--)
                    {
                        var nameAtI = FindByIndex(i);
                        if (string.IsNullOrEmpty(nameAtI)) continue;
                        var finalName = (i + 1).ToString() + nameAtI.Substring(nameAtI.IndexOf('.'));
                        plan.Add((unitPrefix + nameAtI, tempPrefix + nameAtI, unitPrefix + finalName));
                    }
                    var finalSrcName = newIdx.Value.ToString() + sourceActualName.Substring(sourceActualName.IndexOf('.'));
                    plan.Add((unitPrefix + sourceActualName, tempPrefix + sourceActualName, unitPrefix + finalSrcName));
                }

                // Phase 1: copy each source -> temp, then delete source
                foreach (var step in plan)
                {
                    var srcClient = container.GetBlobClient(step.src);
                    if (!await srcClient.ExistsAsync()) continue; // in case of race
                    var props = await srcClient.GetPropertiesAsync();
                    var headers = new BlobHttpHeaders { ContentType = props.Value.ContentType };
                    using var ms = new MemoryStream();
                    await srcClient.DownloadToAsync(ms);
                    ms.Position = 0;
                    await container.GetBlobClient(step.temp).UploadAsync(ms, httpHeaders: headers);
                    await srcClient.DeleteIfExistsAsync();
                }

                // Phase 2: copy temp -> final, then delete temp
                foreach (var step in plan)
                {
                    var tempClient = container.GetBlobClient(step.temp);
                    if (!await tempClient.ExistsAsync()) continue;
                    var props = await tempClient.GetPropertiesAsync();
                    var headers = new BlobHttpHeaders { ContentType = props.Value.ContentType };
                    using var ms = new MemoryStream();
                    await tempClient.DownloadToAsync(ms);
                    ms.Position = 0;
                    await container.GetBlobClient(step.final).UploadAsync(ms, httpHeaders: headers);
                    await tempClient.DeleteIfExistsAsync();
                }

                res.StatusCode = HttpStatusCode.OK;
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(JsonSerializer.Serialize(new { oldName, newName, moved = true }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rename image failed for UnitID {id}, {old} -> {new}", id, oldName, newName);
                return await Error(res, ex.Message);
            }
        }

        // Helpers
        private async Task<string?> GetVinForUnit(int unitId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            var query = "SELECT [VIN] FROM [Units] WHERE [UnitID] = @UnitID";
            using var cmd = new SqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@UnitID", unitId);
            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }

        private BlobContainerClient? ResolveContainerClient()
        {
            var connString =
                _configuration["BlobConnectionString"] ??
                _configuration.GetConnectionString("BlobConnectionString") ??
                _configuration["ConnectionStrings:BlobConnectionString"];

            var baseUrl =
                _configuration["BlobBaseURL"] ??
                _configuration["Blob_URL"] ??
                _configuration.GetConnectionString("BlobBaseURL") ??
                _configuration.GetConnectionString("Blob_URL") ??
                _configuration["ConnectionStrings:BlobBaseURL"] ??
                _configuration["ConnectionStrings:Blob_URL"];

            if (!string.IsNullOrWhiteSpace(connString))
            {
                var containerName = ExtractContainerName(baseUrl) ??
                                    _configuration["BlobContainerName"] ??
                                    _configuration.GetConnectionString("BlobContainerName") ??
                                    _configuration["ConnectionStrings:BlobContainerName"];
                if (!string.IsNullOrWhiteSpace(containerName))
                {
                    return new BlobContainerClient(connString!, containerName!);
                }
            }

            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return new BlobContainerClient(new Uri(baseUrl!), new DefaultAzureCredential());
            }
            return null;
        }

        private string GetBlobPathPrefix()
        {
            // explicit override
            var explicitPrefix =
                _configuration["BlobPathPrefix"] ??
                _configuration.GetConnectionString("BlobPathPrefix") ??
                _configuration["ConnectionStrings:BlobPathPrefix"];
            if (!string.IsNullOrWhiteSpace(explicitPrefix))
            {
                var prefix = explicitPrefix!.Trim().Trim('/') + "/";
                return prefix == "/" ? string.Empty : prefix;
            }

            var baseUrl =
                _configuration["BlobBaseURL"] ??
                _configuration["Blob_URL"] ??
                _configuration.GetConnectionString("BlobBaseURL") ??
                _configuration.GetConnectionString("Blob_URL") ??
                _configuration["ConnectionStrings:BlobBaseURL"] ??
                _configuration["ConnectionStrings:Blob_URL"];
            try
            {
                if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
                var uri = new Uri(baseUrl);
                if (uri.Segments.Length <= 2) return string.Empty;
                var prefix = string.Join(string.Empty, uri.Segments.Skip(2));
                if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith("/")) prefix += "/";
                return prefix;
            }
            catch { return string.Empty; }
        }

        private static string? ExtractContainerName(string? url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                var uri = new Uri(url);
                if (uri.Segments.Length >= 2)
                {
                    return uri.Segments[1].Trim('/');
                }
            }
            catch { }
            return null;
        }

        private static int? TryExtractLeadingInt(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var m = Regex.Match(name, @"^(\d+)");
            if (!m.Success) return null;
            if (int.TryParse(m.Groups[1].Value, out var n)) return n;
            return null;
        }

        private static string? MapExtension(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return null;
            contentType = contentType.ToLowerInvariant();
            if (contentType.Contains("jpeg") || contentType == "image/jpg" || contentType == "image/jpeg") return ".jpg";
            if (contentType == "image/png") return ".png";
            if (contentType == "image/webp") return ".webp";
            if (contentType == "image/gif") return ".gif";
            return null;
        }

        private static string NormalizeExt(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return ".jpg";
            ext = ext.Trim();
            if (!ext.StartsWith('.')) ext = "." + ext;
            return ext.ToLowerInvariant();
        }

        private static bool IsValidImageFileName(string name)
        {
            return Regex.IsMatch(name ?? string.Empty, @"^[0-9]+\.(jpg|jpeg|png|webp|gif)$", RegexOptions.IgnoreCase);
        }

        private static string? GetQuery(HttpRequestData req, string key)
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var v = q.Get(key);
            return v;
        }

        private string BuildPublicUrl(BlobContainerClient container, string fullBlobPath, string fileName, string vin)
        {
            // Prefer configured base URL (it may include CDN and a path prefix)
            var baseUrl =
                _configuration["BlobBaseURL"] ??
                _configuration["Blob_URL"] ??
                _configuration.GetConnectionString("BlobBaseURL") ??
                _configuration.GetConnectionString("Blob_URL") ??
                _configuration["ConnectionStrings:BlobBaseURL"] ??
                _configuration["ConnectionStrings:Blob_URL"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                var trimmed = baseUrl!.TrimEnd('/') + "/" + vin + "/" + fileName;
                return trimmed;
            }
            // fallback to container endpoint
            return container.Uri.ToString().TrimEnd('/') + "/" + fullBlobPath;
        }

        // common responses
        private static void AddCors(HttpResponseData res)
        {
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        private static async Task<HttpResponseData> Error(HttpResponseData res, string message)
        {
            res.StatusCode = HttpStatusCode.InternalServerError;
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return res;
        }

        private static async Task<HttpResponseData> NotFound(HttpResponseData res, string message)
        {
            res.StatusCode = HttpStatusCode.NotFound;
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return res;
        }

        private static async Task<HttpResponseData> BadRequest(HttpResponseData res, string message)
        {
            res.StatusCode = HttpStatusCode.BadRequest;
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return res;
        }

        private static async Task<HttpResponseData> Conflict(HttpResponseData res, string message)
        {
            res.StatusCode = HttpStatusCode.Conflict;
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return res;
        }
    }
}
