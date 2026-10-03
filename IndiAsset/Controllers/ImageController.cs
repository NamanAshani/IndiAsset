using IndiAsset.Services;
using Microsoft.AspNetCore.Mvc;

namespace IndiAsset.Controllers
{
    public class ImageController : Controller
    {
        private readonly GridFsService _gridFsService;
        private readonly ILogger<ImageController> _logger;

        public ImageController(GridFsService gridFsService, ILogger<ImageController> logger)
        {
            _gridFsService = gridFsService;
            _logger = logger;
        }

        [HttpGet("/image/{id}")]
        [HttpGet("/api/images/{id}")]
        [ResponseCache(Duration = 86400 * 30, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetImage(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return NotFound();
            }

            var (stream, contentType, fileName) = await _gridFsService.DownloadFileAsync(id);
            if (stream == null)
            {
                return NotFound();
            }

            // Return stream with cache headers so all browsers & laptops cache it efficiently
            Response.Headers["Cache-Control"] = "public, max-age=2592000, immutable";
            return File(stream, contentType);
        }
    }
}
