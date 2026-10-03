using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace IndiAsset.Services
{
    public class GridFsService
    {
        private readonly IGridFSBucket _bucket;
        private readonly MongoDbService _mongoDbService;
        private readonly ILogger<GridFsService> _logger;

        public GridFsService(
            MongoDbService mongoDbService,
            ILogger<GridFsService> logger)
        {
            _mongoDbService = mongoDbService;
            _logger = logger;
            _bucket = new GridFSBucket(mongoDbService.Database, new GridFSBucketOptions
            {
                BucketName = "fs",
                ChunkSizeBytes = 1048576 // 1MB chunk size
            });
        }

        /// <summary>
        /// Uploads a file stream directly into MongoDB GridFS and returns the generated ObjectId string.
        /// </summary>
        public async Task<string> UploadFileAsync(Stream stream, string fileName, string contentType)
        {
            if (stream == null || stream.Length == 0)
            {
                throw new ArgumentException("File stream cannot be null or empty.", nameof(stream));
            }

            if (stream.CanSeek && stream.Position != 0)
            {
                stream.Position = 0;
            }

            var safeContentType = string.IsNullOrWhiteSpace(contentType)
                ? GetContentTypeFromExtension(fileName)
                : contentType;

            var options = new GridFSUploadOptions
            {
                Metadata = new BsonDocument
                {
                    { "contentType", safeContentType },
                    { "originalName", Path.GetFileName(fileName) },
                    { "uploadedAt", DateTime.UtcNow }
                }
            };

            var id = await _bucket.UploadFromStreamAsync(fileName, stream, options);
            _logger.LogInformation("Uploaded file {FileName} to GridFS with ID: {Id}", fileName, id);
            return id.ToString();
        }

        /// <summary>
        /// Downloads a file by GridFS ObjectId.
        /// </summary>
        public async Task<(Stream? Stream, string ContentType, string FileName)> DownloadFileAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !ObjectId.TryParse(id, out var objectId))
            {
                return (null, string.Empty, string.Empty);
            }

            try
            {
                var filter = Builders<GridFSFileInfo>.Filter.Eq(f => f.Id, objectId);
                using var cursor = await _bucket.FindAsync(filter);
                var fileInfo = await cursor.FirstOrDefaultAsync();

                if (fileInfo == null)
                {
                    return (null, string.Empty, string.Empty);
                }

                var contentType = fileInfo.Metadata != null && fileInfo.Metadata.Contains("contentType")
                    ? fileInfo.Metadata["contentType"].AsString
                    : GetContentTypeFromExtension(fileInfo.Filename);

                var stream = await _bucket.OpenDownloadStreamAsync(objectId);
                return (stream, contentType, fileInfo.Filename);
            }
            catch (GridFSFileNotFoundException)
            {
                return (null, string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error downloading GridFS file {Id}: {Message}", id, ex.Message);
                return (null, string.Empty, string.Empty);
            }
        }

        /// <summary>
        /// Deletes a file from GridFS by its ObjectId string.
        /// </summary>
        public async Task<bool> DeleteFileAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !ObjectId.TryParse(id, out var objectId))
            {
                return false;
            }

            try
            {
                await _bucket.DeleteAsync(objectId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deleting GridFS file {Id}: {Message}", id, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Migrates any existing local uploads from wwwroot/uploads into MongoDB Atlas GridFS
        /// and updates any Asset documents in MongoDB that reference the old /uploads/ paths.
        /// </summary>
        public async Task MigrateExistingLocalUploadsAsync(IWebHostEnvironment environment)
        {
            try
            {
                var assetsDir = Path.Combine(environment.WebRootPath, "uploads", "assets");
                if (!Directory.Exists(assetsDir)) return;

                var files = Directory.GetFiles(assetsDir);
                if (files.Length == 0) return;

                _logger.LogInformation("Checking {Count} local asset images for GridFS migration...", files.Length);

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    var oldUrl1 = $"/uploads/assets/{fileName}";
                    var oldUrl2 = $"uploads/assets/{fileName}";

                    // Check if any asset in MongoDB is using this old local URL
                    var affectedAssets = await _mongoDbService.Assets
                        .Find(a => a.Images.Any(img => img.Url == oldUrl1 || img.Url == oldUrl2 || img.ImageUrl == oldUrl1 || img.ImageUrl == oldUrl2))
                        .ToListAsync();

                    if (affectedAssets.Any())
                    {
                        // Upload local file to GridFS
                        string newId;
                        using (var stream = File.OpenRead(file))
                        {
                            var contentType = GetContentTypeFromExtension(fileName);
                            newId = await UploadFileAsync(stream, fileName, contentType);
                        }

                        var newUrl = $"/image/{newId}";

                        foreach (var asset in affectedAssets)
                        {
                            var modified = false;
                            foreach (var img in asset.Images)
                            {
                                if (img.Url == oldUrl1 || img.Url == oldUrl2 || img.ImageUrl == oldUrl1 || img.ImageUrl == oldUrl2)
                                {
                                    img.Url = newUrl;
                                    img.ImageUrl = newUrl;
                                    modified = true;
                                }
                            }

                            if (modified && !string.IsNullOrEmpty(asset.Id))
                            {
                                await _mongoDbService.Assets.ReplaceOneAsync(a => a.Id == asset.Id, asset);
                                _logger.LogInformation("Migrated image {OldUrl} to {NewUrl} for asset {AssetTitle}", oldUrl1, newUrl, asset.Title);
                            }
                        }

                        // Also update any bookings referencing this old URL
                        var affectedBookings = await _mongoDbService.Bookings
                            .Find(b => b.AssetImageUrl == oldUrl1 || b.AssetImageUrl == oldUrl2)
                            .ToListAsync();

                        foreach (var b in affectedBookings)
                        {
                            b.AssetImageUrl = newUrl;
                            await _mongoDbService.Bookings.ReplaceOneAsync(x => x.Id == b.Id, b);
                        }
                    }
                }

                // Check returns directory as well
                var returnsDir = Path.Combine(environment.WebRootPath, "uploads", "returns");
                if (Directory.Exists(returnsDir))
                {
                    var returnFiles = Directory.GetFiles(returnsDir);
                    foreach (var file in returnFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var oldUrl1 = $"/uploads/returns/{fileName}";
                        var oldUrl2 = $"uploads/returns/{fileName}";

                        var affectedReturns = await _mongoDbService.LeaseReturns
                            .Find(lr => lr.ReturnImageUrls.Contains(oldUrl1) || lr.ReturnImageUrls.Contains(oldUrl2))
                            .ToListAsync();

                        if (affectedReturns.Any())
                        {
                            string newId;
                            using (var stream = File.OpenRead(file))
                            {
                                var contentType = GetContentTypeFromExtension(fileName);
                                newId = await UploadFileAsync(stream, fileName, contentType);
                            }

                            var newUrl = $"/image/{newId}";
                            foreach (var lr in affectedReturns)
                            {
                                for (int i = 0; i < lr.ReturnImageUrls.Count; i++)
                                {
                                    if (lr.ReturnImageUrls[i] == oldUrl1 || lr.ReturnImageUrls[i] == oldUrl2)
                                    {
                                        lr.ReturnImageUrls[i] = newUrl;
                                    }
                                }
                                await _mongoDbService.LeaseReturns.ReplaceOneAsync(x => x.Id == lr.Id, lr);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridFS local migration skipped or encountered an issue: {Message}", ex.Message);
            }
        }

        private static string GetContentTypeFromExtension(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".avif" => "image/avif",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
        }
    }
}
