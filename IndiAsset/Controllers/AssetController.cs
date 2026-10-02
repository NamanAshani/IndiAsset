using IndiAsset.Models;
using IndiAsset.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace IndiAsset.Controllers
{
    public class AssetController : Controller
    {
        private readonly MongoDbService _mongoDbService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AssetAvailabilityService _availabilityService;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private static bool _hasSeeded = false;

        public AssetController(
            MongoDbService mongoDbService,
            UserManager<ApplicationUser> userManager,
            AssetAvailabilityService availabilityService,
            IWebHostEnvironment webHostEnvironment)
        {
            _mongoDbService = mongoDbService;
            _userManager = userManager;
            _availabilityService = availabilityService;
            _webHostEnvironment = webHostEnvironment;
        }

        // ======================================================
        // MARKETPLACE & SEARCH ENGINE
        // ======================================================
        public async Task<IActionResult> Index(
            string? q,
            string? category,
            string? city,
            decimal? minPrice,
            decimal? maxPrice,
            string? availability = "all",
            string? sortBy = "newest")
        {
            await EnsureSeededAsync();

            var filterBuilder = Builders<Asset>.Filter;
            var filter = filterBuilder.Eq(a => a.IsDeleted, false) &
                         filterBuilder.Eq(a => a.IsApproved, true);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var queryTrim = q.Trim();
                var textFilter = filterBuilder.Or(
                    filterBuilder.Regex(a => a.Title, new MongoDB.Bson.BsonRegularExpression(queryTrim, "i")),
                    filterBuilder.Regex(a => a.Description, new MongoDB.Bson.BsonRegularExpression(queryTrim, "i")),
                    filterBuilder.Regex(a => a.Category, new MongoDB.Bson.BsonRegularExpression(queryTrim, "i")),
                    filterBuilder.Regex(a => a.City, new MongoDB.Bson.BsonRegularExpression(queryTrim, "i"))
                );
                filter &= textFilter;
            }

            if (!string.IsNullOrWhiteSpace(category) && category != "All")
            {
                filter &= filterBuilder.Eq(a => a.Category, category);
            }

            if (!string.IsNullOrWhiteSpace(city) && city != "All")
            {
                filter &= filterBuilder.Eq(a => a.City, city);
            }

            if (minPrice.HasValue && minPrice.Value > 0)
            {
                filter &= filterBuilder.Gte(a => a.DailyRent, minPrice.Value);
            }

            if (maxPrice.HasValue && maxPrice.Value > 0)
            {
                filter &= filterBuilder.Lte(a => a.DailyRent, maxPrice.Value);
            }

            var assets = await _mongoDbService.Assets
                .Find(filter)
                .ToListAsync();

            // Evaluate occupancy engine in batch
            var assetIds = assets.Select(a => a.Id ?? string.Empty).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var availabilityDict = await _availabilityService.GetAvailabilityBatchAsync(assetIds);

            // Fetch owner user names for cards
            var ownerIds = assets.Select(a => a.OwnerId).Distinct().ToList();
            var owners = await _mongoDbService.Bookings.Database
                .GetCollection<ApplicationUser>("Users")
                .Find(u => ownerIds.Contains(u.Id))
                .ToListAsync();
            var ownerNameMap = owners.ToDictionary(
                u => u.Id,
                u => !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : (u.Email ?? "Asset Owner")
            );

            var currentUserId = _userManager.GetUserId(User);

            var cardViewModels = assets.Select(asset =>
            {
                var id = asset.Id ?? string.Empty;
                availabilityDict.TryGetValue(id, out var availInfo);
                ownerNameMap.TryGetValue(asset.OwnerId, out var ownerName);

                var isOccupied = availInfo?.IsCurrentlyOccupied ?? (!asset.IsAvailable);
                var occupiedUntil = availInfo?.OccupiedUntil;
                var availableFrom = availInfo?.AvailableFrom ?? DateTime.UtcNow.Date;

                return new AssetCardViewModel
                {
                    Id = id,
                    Title = asset.Title,
                    Description = asset.Description,
                    Category = asset.Category,
                    DailyRent = asset.DailyRent,
                    SecurityDeposit = asset.SecurityDeposit,
                    City = asset.City,
                    PrimaryImageUrl = asset.Images.FirstOrDefault(i => i.IsPrimary)?.Url ?? asset.Images.FirstOrDefault()?.Url,
                    OwnerId = asset.OwnerId,
                    OwnerName = ownerName ?? "Verified Partner",
                    IsCurrentlyOccupied = isOccupied,
                    OccupiedUntil = occupiedUntil,
                    AvailableFrom = availableFrom,
                    IsOwner = !string.IsNullOrEmpty(currentUserId) && asset.OwnerId == currentUserId
                };
            }).ToList();

            // Apply Occupancy Filter
            if (availability == "available")
            {
                cardViewModels = cardViewModels.Where(c => !c.IsCurrentlyOccupied).ToList();
            }
            else if (availability == "occupied")
            {
                cardViewModels = cardViewModels.Where(c => c.IsCurrentlyOccupied).ToList();
            }

            // Apply Sorting
            cardViewModels = sortBy switch
            {
                "price_asc" => cardViewModels.OrderBy(c => c.DailyRent).ToList(),
                "price_desc" => cardViewModels.OrderByDescending(c => c.DailyRent).ToList(),
                "title" => cardViewModels.OrderBy(c => c.Title).ToList(),
                _ => cardViewModels.OrderByDescending(c => c.AvailableFrom).ToList()
            };

            // Get distinct categories and cities for filters
            var allActiveAssets = await _mongoDbService.Assets
                .Find(a => !a.IsDeleted && a.IsApproved)
                .ToListAsync();

            var categories = allActiveAssets
                .Select(a => a.Category)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            var cities = allActiveAssets
                .Select(a => a.City)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .OrderBy(c => c!)
                .Select(c => c!)
                .ToList();

            var model = new AssetSearchViewModel
            {
                SearchQuery = q,
                SelectedCategory = category,
                City = city,
                MinPrice = minPrice,
                MaxPrice = maxPrice,
                AvailabilityFilter = availability,
                SortBy = sortBy,
                Assets = cardViewModels,
                Categories = categories,
                Cities = cities,
                TotalCount = cardViewModels.Count
            };

            return View(model);
        }

        // ======================================================
        // ASSET DETAILS PAGE
        // ======================================================
        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == id && !a.IsDeleted)
                .FirstOrDefaultAsync();

            if (asset == null) return NotFound();

            var owner = await _userManager.FindByIdAsync(asset.OwnerId);
            var availability = await _availabilityService.GetAvailabilityAsync(asset);
            var currentUserId = _userManager.GetUserId(User);
            var isOwner = !string.IsNullOrEmpty(currentUserId) && currentUserId == asset.OwnerId;

            var defaultStartDate = availability.IsCurrentlyOccupied && availability.OccupiedUntil.HasValue
                ? availability.OccupiedUntil.Value.Date.AddDays(1)
                : DateTime.UtcNow.Date.AddDays(1);

            var defaultEndDate = defaultStartDate.AddDays(3);

            var viewModel = new AssetDetailsViewModel
            {
                Asset = asset,
                Owner = owner,
                IsCurrentlyOccupied = availability.IsCurrentlyOccupied,
                OccupiedUntil = availability.OccupiedUntil,
                AvailableFrom = availability.AvailableFrom,
                ActiveBookingsCount = availability.ActiveBookingsCount,
                ConfirmedBookings = availability.ConfirmedBookings,
                IsOwner = isOwner,
                CanBook = !isOwner && User.Identity?.IsAuthenticated == true,
                BookingForm = new BookingCreateViewModel
                {
                    AssetId = asset.Id ?? string.Empty,
                    StartDate = defaultStartDate,
                    EndDate = defaultEndDate
                }
            };

            return View(viewModel);
        }

        // ======================================================
        // CREATE LISTING (OWNER)
        // ======================================================
        [Authorize]
        [HttpGet]
        public IActionResult Create()
        {
            var model = new AssetCreateEditViewModel();
            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AssetCreateEditViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var currentUserId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(currentUserId)) return Challenge();

            // Determine effective category (handle "Other" with custom entered category)
            var effectiveCategory = model.Category.Trim();
            if (string.Equals(model.Category, "Other", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(model.CustomCategory))
            {
                effectiveCategory = model.CustomCategory.Trim();
            }

            var asset = new Asset
            {
                OwnerId = currentUserId,
                Title = model.Title.Trim(),
                Description = model.Description.Trim(),
                Category = effectiveCategory,
                DailyRent = model.DailyRent,
                SecurityDeposit = model.SecurityDeposit,
                City = model.City?.Trim(),
                State = model.State?.Trim(),
                IsAvailable = model.IsAvailable,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // 1. Process Directly Uploaded Image Files
            if (model.UploadedImages != null && model.UploadedImages.Any())
            {
                var uploadDir = Path.Combine(_webHostEnvironment.WebRootPath, "uploads", "assets");
                if (!Directory.Exists(uploadDir))
                {
                    Directory.CreateDirectory(uploadDir);
                }

                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
                foreach (var file in model.UploadedImages)
                {
                    if (file.Length > 0 && file.Length < 25 * 1024 * 1024)
                    {
                        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                        if (allowedExtensions.Contains(ext))
                        {
                            var uniqueName = $"{Guid.NewGuid():N}{ext}";
                            var fullPath = Path.Combine(uploadDir, uniqueName);
                            using (var stream = new FileStream(fullPath, FileMode.Create))
                            {
                                await file.CopyToAsync(stream);
                            }

                            asset.Images.Add(new AssetImage
                            {
                                ImageUrl = $"/uploads/assets/{uniqueName}",
                                Url = $"/uploads/assets/{uniqueName}",
                                IsPrimary = asset.Images.Count == 0,
                                Caption = model.Title
                            });
                        }
                    }
                }
            }

            // 2. Process Image URLs if also provided
            if (!string.IsNullOrWhiteSpace(model.ImageUrls))
            {
                var urls = model.ImageUrls
                    .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(u => u.Trim())
                    .Where(u => !string.IsNullOrEmpty(u))
                    .ToList();

                foreach (var url in urls)
                {
                    asset.Images.Add(new AssetImage
                    {
                        ImageUrl = url,
                        Url = url,
                        IsPrimary = asset.Images.Count == 0,
                        Caption = model.Title
                    });
                }
            }

            // Fallback default image if none uploaded
            if (!asset.Images.Any())
            {
                asset.Images.Add(new AssetImage
                {
                    ImageUrl = "https://images.unsplash.com/photo-1581092160607-ee22621dd758?auto=format&fit=crop&w=1000&q=80",
                    Url = "https://images.unsplash.com/photo-1581092160607-ee22621dd758?auto=format&fit=crop&w=1000&q=80",
                    IsPrimary = true,
                    Caption = asset.Title
                });
            }

            // Clean specifications
            if (model.Specifications != null)
            {
                asset.Specifications = model.Specifications
                    .Where(s => !string.IsNullOrWhiteSpace(s.Key) && !string.IsNullOrWhiteSpace(s.Value))
                    .ToList();
            }

            await _mongoDbService.Assets.InsertOneAsync(asset);
            TempData["SuccessMessage"] = "Asset listed successfully on the marketplace!";
            return RedirectToAction(nameof(Details), new { id = asset.Id });
        }

        // ======================================================
        // EDIT LISTING (OWNER)
        // ======================================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Edit(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == id && !a.IsDeleted)
                .FirstOrDefaultAsync();

            if (asset == null) return NotFound();
            if (asset.OwnerId != currentUserId) return Forbid();

            var standardCategories = new[]
            {
                "Construction & Earthmoving", "Heavy Machinery", "Cinema & Media",
                "Agriculture Equipment", "Power & Energy", "Transportation & Logistics", "Industrial Tools"
            };

            var isStandard = standardCategories.Contains(asset.Category);
            var categoryVal = isStandard ? asset.Category : "Other";
            var customCatVal = isStandard ? null : asset.Category;

            var model = new AssetCreateEditViewModel
            {
                Id = asset.Id,
                Title = asset.Title,
                Description = asset.Description,
                Category = categoryVal,
                CustomCategory = customCatVal,
                DailyRent = asset.DailyRent,
                SecurityDeposit = asset.SecurityDeposit,
                City = asset.City,
                State = asset.State,
                IsAvailable = asset.IsAvailable,
                ExistingImageUrls = asset.Images.Select(i => i.Url).ToList(),
                Specifications = asset.Specifications
            };

            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(AssetCreateEditViewModel model)
        {
            if (string.IsNullOrEmpty(model.Id)) return NotFound();
            if (!ModelState.IsValid) return View(model);

            var currentUserId = _userManager.GetUserId(User);
            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == model.Id && !a.IsDeleted)
                .FirstOrDefaultAsync();

            if (asset == null) return NotFound();
            if (asset.OwnerId != currentUserId) return Forbid();

            // Determine effective category
            var effectiveCategory = model.Category.Trim();
            if (string.Equals(model.Category, "Other", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(model.CustomCategory))
            {
                effectiveCategory = model.CustomCategory.Trim();
            }

            asset.Title = model.Title.Trim();
            asset.Description = model.Description.Trim();
            asset.Category = effectiveCategory;
            asset.DailyRent = model.DailyRent;
            asset.SecurityDeposit = model.SecurityDeposit;
            asset.City = model.City?.Trim();
            asset.State = model.State?.Trim();
            asset.IsAvailable = model.IsAvailable;
            asset.UpdatedAt = DateTime.UtcNow;

            // Retain selected existing images if specified, otherwise keep all
            if (model.ExistingImageUrls != null && model.ExistingImageUrls.Any(u => !string.IsNullOrEmpty(u)))
            {
                var validUrls = model.ExistingImageUrls.Where(u => !string.IsNullOrEmpty(u)).ToList();
                asset.Images = asset.Images.Where(img => validUrls.Contains(img.Url)).ToList();
            }
            // If no existing URLs sent and no new uploads, keep current images as-is

            // Append newly uploaded images
            if (model.UploadedImages != null && model.UploadedImages.Any())
            {
                var uploadDir = Path.Combine(_webHostEnvironment.WebRootPath, "uploads", "assets");
                if (!Directory.Exists(uploadDir))
                {
                    Directory.CreateDirectory(uploadDir);
                }

                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
                foreach (var file in model.UploadedImages)
                {
                    if (file.Length > 0 && file.Length < 25 * 1024 * 1024)
                    {
                        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                        if (allowedExtensions.Contains(ext))
                        {
                            var uniqueName = $"{Guid.NewGuid():N}{ext}";
                            var fullPath = Path.Combine(uploadDir, uniqueName);
                            using (var stream = new FileStream(fullPath, FileMode.Create))
                            {
                                await file.CopyToAsync(stream);
                            }

                            asset.Images.Add(new AssetImage
                            {
                                ImageUrl = $"/uploads/assets/{uniqueName}",
                                Url = $"/uploads/assets/{uniqueName}",
                                IsPrimary = asset.Images.Count == 0,
                                Caption = model.Title
                            });
                        }
                    }
                }
            }

            if (model.Specifications != null)
            {
                asset.Specifications = model.Specifications
                    .Where(s => !string.IsNullOrWhiteSpace(s.Key) && !string.IsNullOrWhiteSpace(s.Value))
                    .ToList();
            }

            await _mongoDbService.Assets.ReplaceOneAsync(a => a.Id == asset.Id, asset);
            TempData["SuccessMessage"] = "Asset updated successfully!";
            return RedirectToAction(nameof(Details), new { id = asset.Id });
        }

        // ======================================================
        // OWNER'S DASHBOARD: MY ASSETS
        // ======================================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> MyAssets()
        {
            var currentUserId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(currentUserId)) return Challenge();

            var assets = await _mongoDbService.Assets
                .Find(a => a.OwnerId == currentUserId && !a.IsDeleted)
                .SortByDescending(a => a.CreatedAt)
                .ToListAsync();

            var assetIds = assets.Select(a => a.Id ?? string.Empty).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var availabilityDict = await _availabilityService.GetAvailabilityBatchAsync(assetIds);

            var cardViewModels = assets.Select(asset =>
            {
                var id = asset.Id ?? string.Empty;
                availabilityDict.TryGetValue(id, out var availInfo);

                return new AssetCardViewModel
                {
                    Id = id,
                    Title = asset.Title,
                    Description = asset.Description,
                    Category = asset.Category,
                    DailyRent = asset.DailyRent,
                    SecurityDeposit = asset.SecurityDeposit,
                    City = asset.City,
                    PrimaryImageUrl = asset.Images.FirstOrDefault(i => i.IsPrimary)?.Url ?? asset.Images.FirstOrDefault()?.Url,
                    OwnerId = asset.OwnerId,
                    OwnerName = "You",
                    IsCurrentlyOccupied = availInfo?.IsCurrentlyOccupied ?? (!asset.IsAvailable),
                    OccupiedUntil = availInfo?.OccupiedUntil,
                    AvailableFrom = availInfo?.AvailableFrom ?? DateTime.UtcNow.Date,
                    IsOwner = true
                };
            }).ToList();

            return View(cardViewModels);
        }

        // ======================================================
        // TOGGLE ASSET AVAILABILITY (OWNER)
        // ======================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleAvailability(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == id && !a.IsDeleted)
                .FirstOrDefaultAsync();

            if (asset == null) return NotFound();
            if (asset.OwnerId != currentUserId) return Forbid();

            var update = Builders<Asset>.Update
                .Set(a => a.IsAvailable, !asset.IsAvailable)
                .Set(a => a.UpdatedAt, DateTime.UtcNow);

            await _mongoDbService.Assets.UpdateOneAsync(a => a.Id == id, update);

            TempData["SuccessMessage"] = asset.IsAvailable
                ? "Asset is now set to Paused / Unavailable."
                : "Asset is now marked Available for leasing!";

            return RedirectToAction(nameof(MyAssets));
        }

        // ======================================================
        // SOFT DELETE ASSET (OWNER)
        // ======================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == id && !a.IsDeleted)
                .FirstOrDefaultAsync();

            if (asset == null) return NotFound();
            if (asset.OwnerId != currentUserId) return Forbid();

            var update = Builders<Asset>.Update
                .Set(a => a.IsDeleted, true)
                .Set(a => a.UpdatedAt, DateTime.UtcNow);

            await _mongoDbService.Assets.UpdateOneAsync(a => a.Id == id, update);
            TempData["SuccessMessage"] = "Asset listing removed.";
            return RedirectToAction(nameof(MyAssets));
        }

        // ======================================================
        // AUTO-SEED SAMPLE ASSETS & REALISTIC OCCUPANCY
        // ======================================================
        private async Task EnsureSeededAsync()
        {
            if (_hasSeeded) return;

            var count = await _mongoDbService.Assets.CountDocumentsAsync(a => !a.IsDeleted);
            if (count > 0)
            {
                _hasSeeded = true;
                return;
            }

            // Find an existing user or fallback to system owner
            var firstUser = await _mongoDbService.Bookings.Database
                .GetCollection<ApplicationUser>("Users")
                .Find(_ => true)
                .FirstOrDefaultAsync();

            var ownerId = firstUser?.Id ?? "system_owner";

            var samples = new List<Asset>
            {
                new Asset
                {
                    OwnerId = ownerId,
                    Title = "JCB 3DX Super Backhoe Loader (Heavy Earthmover)",
                    Description = "Reliable 76 hp high-performance heavy earthmover excavator for construction, trenching, digging, and material handling. Includes experienced operator option and fuel consumption tracker.",
                    Category = "Construction & Earthmoving",
                    DailyRent = 4500m,
                    SecurityDeposit = 20000m,
                    City = "Bengaluru",
                    State = "Karnataka",
                    IsAvailable = true,
                    IsApproved = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-10),
                    Images = new List<AssetImage>
                    {
                        new AssetImage { Url = "https://images.unsplash.com/photo-1578328819058-b69f3a3b0f6b?auto=format&fit=crop&w=1000&q=80", IsPrimary = true, Caption = "JCB Backhoe Loader Front View" }
                    },
                    Specifications = new List<AssetSpecification>
                    {
                        new AssetSpecification { Key = "Engine Power", Value = "76 HP @ 2200 RPM" },
                        new AssetSpecification { Key = "Operating Weight", Value = "7,460 kg" },
                        new AssetSpecification { Key = "Max Dig Depth", Value = "4.77 meters" },
                        new AssetSpecification { Key = "Fuel Type", Value = "Diesel" }
                    }
                },
                new Asset
                {
                    OwnerId = ownerId,
                    Title = "Liebherr 85 EC-B 5b Tower Crane (35m Jib)",
                    Description = "Precision city tower crane suitable for high-rise commercial construction and industrial framing. Fast erection capability, intelligent anti-collision telemetry and high hoist velocity.",
                    Category = "Heavy Machinery",
                    DailyRent = 12000m,
                    SecurityDeposit = 75000m,
                    City = "Mumbai",
                    State = "Maharashtra",
                    IsAvailable = true,
                    IsApproved = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-8),
                    Images = new List<AssetImage>
                    {
                        new AssetImage { Url = "https://images.unsplash.com/photo-1541888946425-d0fbb186f5f7?auto=format&fit=crop&w=1000&q=80", IsPrimary = true, Caption = "Tower Crane at Construction Site" }
                    },
                    Specifications = new List<AssetSpecification>
                    {
                        new AssetSpecification { Key = "Max Load Capacity", Value = "5,000 kg" },
                        new AssetSpecification { Key = "Jib Length", Value = "35 meters" },
                        new AssetSpecification { Key = "Max Hook Height", Value = "42 meters" }
                    }
                },
                new Asset
                {
                    OwnerId = ownerId,
                    Title = "Sony FX6 Cinema Camera Production Kit (4K Full Frame)",
                    Description = "Full cinema rig ready for commercial shoots, OTT productions, and indie films. Includes 24-70mm f/2.8 GM lens, 3x V-Mount 150Wh batteries, Tilta cage, and 2x 160GB CFexpress Type A cards.",
                    Category = "Cinema & Media",
                    DailyRent = 3800m,
                    SecurityDeposit = 30000m,
                    City = "Hyderabad",
                    State = "Telangana",
                    IsAvailable = true,
                    IsApproved = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-6),
                    Images = new List<AssetImage>
                    {
                        new AssetImage { Url = "https://images.unsplash.com/photo-1516035069371-29a1b244cc32?auto=format&fit=crop&w=1000&q=80", IsPrimary = true, Caption = "Cinema Camera Production Rig" }
                    },
                    Specifications = new List<AssetSpecification>
                    {
                        new AssetSpecification { Key = "Sensor", Value = "4K Full-Frame 10.2MP Exmor R" },
                        new AssetSpecification { Key = "Dynamic Range", Value = "15+ stops (S-Log3)" },
                        new AssetSpecification { Key = "Max Frame Rate", Value = "4K 120p, FHD 240p" }
                    }
                },
                new Asset
                {
                    OwnerId = ownerId,
                    Title = "John Deere 5050D 4WD Utility Tractor (50 HP)",
                    Description = "Versatile 50 HP tractor equipped with power steering, dual clutch, and heavy-duty 3-point linkage. Ideal for agricultural tilling, seeding, harvesting, and regional cargo transport.",
                    Category = "Agriculture Equipment",
                    DailyRent = 2200m,
                    SecurityDeposit = 15000m,
                    City = "Pune",
                    State = "Maharashtra",
                    IsAvailable = true,
                    IsApproved = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-4),
                    Images = new List<AssetImage>
                    {
                        new AssetImage { Url = "https://images.unsplash.com/photo-1592982537447-7440770cbfc9?auto=format&fit=crop&w=1000&q=80", IsPrimary = true, Caption = "Utility Tractor" }
                    },
                    Specifications = new List<AssetSpecification>
                    {
                        new AssetSpecification { Key = "Engine", Value = "3 Cylinder Turbocharged Diesel" },
                        new AssetSpecification { Key = "Lift Capacity", Value = "1,600 kgf" },
                        new AssetSpecification { Key = "Drive", Value = "4-Wheel Drive" }
                    }
                },
                new Asset
                {
                    OwnerId = ownerId,
                    Title = "Cummins 125 kVA Silent Industrial Diesel Generator",
                    Description = "Ultra-quiet soundproof acoustic enclosure DG set for continuous power backup at event venues, construction sites, and manufacturing hubs. CPCB II compliant emissions.",
                    Category = "Power & Energy",
                    DailyRent = 5500m,
                    SecurityDeposit = 25000m,
                    City = "Delhi",
                    State = "Delhi NCR",
                    IsAvailable = true,
                    IsApproved = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-2),
                    Images = new List<AssetImage>
                    {
                        new AssetImage { Url = "https://images.unsplash.com/photo-1581092160607-ee22621dd758?auto=format&fit=crop&w=1000&q=80", IsPrimary = true, Caption = "Industrial Silent Generator" }
                    },
                    Specifications = new List<AssetSpecification>
                    {
                        new AssetSpecification { Key = "Output Capacity", Value = "125 kVA / 100 kWe" },
                        new AssetSpecification { Key = "Sound Level", Value = "< 75 dBA at 1 meter" },
                        new AssetSpecification { Key = "Fuel Tank Capacity", Value = "250 Liters" }
                    }
                },
                new Asset
                {
                    OwnerId = ownerId,
                    Title = "DJI Inspire 3 Raw 8K Cinema Drone with Dual Control",
                    Description = "Flagship cinematic aerial drone featuring full-frame 8K ProRes RAW recording, RTK centimeter-level positioning, waypoint 3.0, and dual pilot/camera operator remote controllers.",
                    Category = "Cinema & Media",
                    DailyRent = 6500m,
                    SecurityDeposit = 50000m,
                    City = "Bengaluru",
                    State = "Karnataka",
                    IsAvailable = true,
                    IsApproved = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                    Images = new List<AssetImage>
                    {
                        new AssetImage { Url = "https://images.unsplash.com/photo-1527977966376-1c8408f9f108?auto=format&fit=crop&w=1000&q=80", IsPrimary = true, Caption = "Professional 8K Aerial Drone" }
                    },
                    Specifications = new List<AssetSpecification>
                    {
                        new AssetSpecification { Key = "Max Resolution", Value = "8K 75fps ProRes RAW" },
                        new AssetSpecification { Key = "Flight Time", Value = "Up to 28 minutes per pair" },
                        new AssetSpecification { Key = "Max Transmission", Value = "15 km O3 Pro" }
                    }
                }
            };

            await _mongoDbService.Assets.InsertManyAsync(samples);

            // Create a realistic active booking for the 2nd asset (Liebherr Crane) so occupancy engine demonstrates occupied status
            var occupiedAsset = samples[1];
            if (occupiedAsset.Id != null)
            {
                var sampleBooking = new Booking
                {
                    AssetId = occupiedAsset.Id,
                    AssetTitle = occupiedAsset.Title,
                    AssetImageUrl = occupiedAsset.Images.FirstOrDefault()?.Url,
                    RenterId = "demo_renter_partner",
                    OwnerId = ownerId,
                    StartDate = DateTime.UtcNow.Date.AddDays(-2),
                    EndDate = DateTime.UtcNow.Date.AddDays(5), // Occupied until 5 days from now
                    DailyRent = occupiedAsset.DailyRent,
                    TotalRent = occupiedAsset.DailyRent * 7,
                    SecurityDeposit = occupiedAsset.SecurityDeposit,
                    TotalAmount = (occupiedAsset.DailyRent * 7) + occupiedAsset.SecurityDeposit,
                    Status = BookingStatus.Active,
                    Notes = "High-rise commercial construction lease",
                    CreatedAt = DateTime.UtcNow.AddDays(-3),
                    ApprovedAt = DateTime.UtcNow.AddDays(-2)
                };
                await _mongoDbService.Bookings.InsertOneAsync(sampleBooking);
            }

            _hasSeeded = true;
        }
    }
}
