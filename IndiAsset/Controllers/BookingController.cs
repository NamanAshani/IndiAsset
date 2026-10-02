using IndiAsset.Models;
using IndiAsset.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace IndiAsset.Controllers
{
    [Authorize]
    public class BookingController : Controller
    {
        private readonly MongoDbService _mongoDbService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AssetAvailabilityService _availabilityService;

        public BookingController(
            MongoDbService mongoDbService,
            UserManager<ApplicationUser> userManager,
            AssetAvailabilityService availabilityService)
        {
            _mongoDbService = mongoDbService;
            _userManager = userManager;
            _availabilityService = availabilityService;
        }

        // ======================================================
        // SUBMIT LEASE / BOOKING REQUEST
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(BookingCreateViewModel model)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return Challenge();

            if (string.IsNullOrEmpty(model.AssetId))
            {
                return RedirectToAction("Index", "Asset");
            }

            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == model.AssetId && !a.IsDeleted)
                .FirstOrDefaultAsync();

            if (asset == null)
            {
                TempData["ErrorMessage"] = "The requested asset was not found.";
                return RedirectToAction("Index", "Asset");
            }

            // Cannot lease your own asset
            if (asset.OwnerId == currentUser.Id)
            {
                TempData["ErrorMessage"] = "You cannot lease your own asset.";
                return RedirectToAction("Details", "Asset", new { id = model.AssetId });
            }

            // Date validation
            var now = DateTime.UtcNow.Date;
            if (model.StartDate.Date < now)
            {
                TempData["ErrorMessage"] = "Lease start date cannot be in the past.";
                return RedirectToAction("Details", "Asset", new { id = model.AssetId });
            }

            if (model.EndDate.Date <= model.StartDate.Date)
            {
                TempData["ErrorMessage"] = "Lease end date must be after start date.";
                return RedirectToAction("Details", "Asset", new { id = model.AssetId });
            }

            // Real-time Occupancy & Conflict Check
            var isAvailable = await _availabilityService.IsDateRangeAvailableAsync(
                model.AssetId,
                model.StartDate.Date,
                model.EndDate.Date);

            if (!isAvailable)
            {
                TempData["ErrorMessage"] = "This asset is currently occupied or already booked during your requested dates. Please choose another date range.";
                return RedirectToAction("Details", "Asset", new { id = model.AssetId });
            }

            var days = Math.Max(1, (int)(model.EndDate.Date - model.StartDate.Date).TotalDays);
            var totalRent = days * asset.DailyRent;
            var securityDeposit = asset.SecurityDeposit;
            var totalAmount = totalRent + securityDeposit;

            var booking = new Booking
            {
                AssetId = asset.Id ?? string.Empty,
                AssetTitle = asset.Title,
                AssetImageUrl = asset.Images.FirstOrDefault(i => i.IsPrimary)?.Url ?? asset.Images.FirstOrDefault()?.Url,
                RenterId = currentUser.Id,
                OwnerId = asset.OwnerId,
                StartDate = model.StartDate.Date,
                EndDate = model.EndDate.Date,
                DailyRent = asset.DailyRent,
                TotalRent = totalRent,
                SecurityDeposit = securityDeposit,
                TotalAmount = totalAmount,
                Status = BookingStatus.Pending,
                Notes = model.Notes?.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            await _mongoDbService.Bookings.InsertOneAsync(booking);

            // Notify Asset Owner
            var notification = new Notification
            {
                UserId = asset.OwnerId,
                Title = "New Lease Request Received",
                Message = $"{currentUser.FullName ?? currentUser.Email} requested to lease '{asset.Title}' from {booking.StartDate:dd MMM yyyy} to {booking.EndDate:dd MMM yyyy}.",
                Type = "BookingRequest",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = $"Lease request for '{asset.Title}' submitted successfully! The owner has been notified.";
            return RedirectToAction(nameof(MyBookings));
        }

        // ======================================================
        // DUAL LEASE DASHBOARD (RENTER & OWNER TABS)
        // ======================================================
        [HttpGet]
        public async Task<IActionResult> MyBookings()
        {
            var currentUserId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(currentUserId)) return Challenge();

            // Bookings where I am taking on lease (Renter)
            var renterBookings = await _mongoDbService.Bookings
                .Find(b => b.RenterId == currentUserId)
                .SortByDescending(b => b.CreatedAt)
                .ToListAsync();

            // Bookings where other users are leasing my assets (Owner)
            var ownerBookings = await _mongoDbService.Bookings
                .Find(b => b.OwnerId == currentUserId)
                .SortByDescending(b => b.CreatedAt)
                .ToListAsync();

            // Collect all counterpart user IDs
            var userIds = renterBookings.Select(b => b.OwnerId)
                .Concat(ownerBookings.Select(b => b.RenterId))
                .Distinct()
                .ToList();

            var users = await _mongoDbService.Bookings.Database
                .GetCollection<ApplicationUser>("Users")
                .Find(u => userIds.Contains(u.Id))
                .ToListAsync();

            var userMap = users.ToDictionary(
                u => u.Id,
                u => !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : (u.Email ?? "User")
            );

            // Fetch categories for asset titles if needed
            var assetIds = renterBookings.Select(b => b.AssetId)
                .Concat(ownerBookings.Select(b => b.AssetId))
                .Distinct()
                .ToList();

            var assets = await _mongoDbService.Assets
                .Find(a => a.Id != null && assetIds.Contains(a.Id))
                .ToListAsync();

            var assetCategoryMap = assets.ToDictionary(a => a.Id ?? string.Empty, a => a.Category);

            var viewModel = new MyBookingsViewModel
            {
                AsRenterBookings = renterBookings.Select(b => new BookingItemViewModel
                {
                    BookingId = b.Id ?? string.Empty,
                    AssetId = b.AssetId,
                    AssetTitle = b.AssetTitle ?? "Asset",
                    AssetImageUrl = b.AssetImageUrl,
                    Category = assetCategoryMap.GetValueOrDefault(b.AssetId, "Equipment"),
                    OtherUserId = b.OwnerId,
                    OtherUserName = userMap.GetValueOrDefault(b.OwnerId, "Asset Owner"),
                    StartDate = b.StartDate,
                    EndDate = b.EndDate,
                    DailyRent = b.DailyRent,
                    TotalRent = b.TotalRent,
                    SecurityDeposit = b.SecurityDeposit,
                    TotalAmount = b.TotalAmount,
                    Status = b.Status,
                    CreatedAt = b.CreatedAt,
                    IsOwner = false
                }).ToList(),

                AsOwnerBookings = ownerBookings.Select(b => new BookingItemViewModel
                {
                    BookingId = b.Id ?? string.Empty,
                    AssetId = b.AssetId,
                    AssetTitle = b.AssetTitle ?? "Asset",
                    AssetImageUrl = b.AssetImageUrl,
                    Category = assetCategoryMap.GetValueOrDefault(b.AssetId, "Equipment"),
                    OtherUserId = b.RenterId,
                    OtherUserName = userMap.GetValueOrDefault(b.RenterId, "Lease Taker"),
                    StartDate = b.StartDate,
                    EndDate = b.EndDate,
                    DailyRent = b.DailyRent,
                    TotalRent = b.TotalRent,
                    SecurityDeposit = b.SecurityDeposit,
                    TotalAmount = b.TotalAmount,
                    Status = b.Status,
                    CreatedAt = b.CreatedAt,
                    IsOwner = true
                }).ToList()
            };

            return View(viewModel);
        }

        // ======================================================
        // OWNER ACTIONS: APPROVE / REJECT LEASE REQUEST
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == id)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId) return Forbid();

            // Set to Active if start date is today or earlier, otherwise Approved
            var newStatus = booking.StartDate <= DateTime.UtcNow.Date ? BookingStatus.Active : BookingStatus.Approved;

            var update = Builders<Booking>.Update
                .Set(b => b.Status, newStatus)
                .Set(b => b.ApprovedAt, DateTime.UtcNow);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == id, update);

            // Notify renter
            var notification = new Notification
            {
                UserId = booking.RenterId,
                Title = "Lease Request Approved! 🎉",
                Message = $"Your lease request for '{booking.AssetTitle}' ({booking.StartDate:dd MMM} - {booking.EndDate:dd MMM}) has been approved.",
                Type = "BookingApproved",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = "Lease request approved!";
            return RedirectToAction(nameof(MyBookings));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == id)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId) return Forbid();

            var update = Builders<Booking>.Update
                .Set(b => b.Status, BookingStatus.Rejected)
                .Set(b => b.RejectedAt, DateTime.UtcNow);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == id, update);

            // Notify renter
            var notification = new Notification
            {
                UserId = booking.RenterId,
                Title = "Lease Request Declined",
                Message = $"Your lease request for '{booking.AssetTitle}' was declined by the owner.",
                Type = "BookingRejected",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = "Lease request rejected.";
            return RedirectToAction(nameof(MyBookings));
        }

        // ======================================================
        // CANCEL LEASE
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == id)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId && booking.OwnerId != currentUserId) return Forbid();

            var update = Builders<Booking>.Update
                .Set(b => b.Status, BookingStatus.Cancelled)
                .Set(b => b.CancelledAt, DateTime.UtcNow);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == id, update);

            TempData["SuccessMessage"] = "Lease cancelled successfully.";
            return RedirectToAction(nameof(MyBookings));
        }
    }
}
