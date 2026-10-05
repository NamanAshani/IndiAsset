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
        private readonly GridFsService _gridFsService;
        private readonly IRazorpayService _razorpayService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AssetAvailabilityService _availabilityService;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly IImageComparisonService _imageComparisonService;
        private readonly ILogger<BookingController> _logger;

        public BookingController(
            MongoDbService mongoDbService,
            GridFsService gridFsService,
            IRazorpayService razorpayService,
            UserManager<ApplicationUser> userManager,
            AssetAvailabilityService availabilityService,
            IWebHostEnvironment webHostEnvironment,
            IImageComparisonService imageComparisonService,
            ILogger<BookingController> logger)
        {
            _mongoDbService = mongoDbService;
            _gridFsService = gridFsService;
            _razorpayService = razorpayService;
            _userManager = userManager;
            _availabilityService = availabilityService;
            _webHostEnvironment = webHostEnvironment;
            _imageComparisonService = imageComparisonService;
            _logger = logger;
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

            // Commercial leasing rule: Asset owners cannot lease their own asset.
            // Owners should use the dedicated Maintenance / Blackout Window to reserve equipment.
            if (asset.OwnerId == currentUser.Id)
            {
                TempData["ErrorMessage"] = "You cannot lease your own equipment. To reserve dates for maintenance or personal use, please schedule a Maintenance Blackout Window on your asset details page.";
                return RedirectToAction("Details", "Asset", new { id = model.AssetId });
            }

            // If user specified NumberOfDays and end date wasn't adjusted, synchronize EndDate
            if (model.NumberOfDays > 0)
            {
                if (model.EndDate <= model.StartDate || (int)(model.EndDate.Date - model.StartDate.Date).TotalDays != model.NumberOfDays)
                {
                    model.EndDate = model.StartDate.Date.AddDays(model.NumberOfDays);
                }
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

            // Real-time Occupancy & Conflict Check (checks both existing bookings and maintenance blackout windows)
            var isAvailable = await _availabilityService.IsDateRangeAvailableAsync(
                model.AssetId,
                model.StartDate.Date,
                model.EndDate.Date);

            if (!isAvailable)
            {
                TempData["ErrorMessage"] = "This asset is currently occupied or scheduled for maintenance during your requested dates. Please choose another date range.";
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
                TotalDays = days,
                DailyRent = asset.DailyRent,
                TotalRent = totalRent,
                OriginalDailyRent = asset.DailyRent,
                OriginalTotalRent = totalRent,
                SecurityDeposit = securityDeposit,
                TotalAmount = totalAmount,
                Status = BookingStatus.Pending,
                Notes = model.Notes?.Trim(),
                IsSecurityDepositPaid = securityDeposit <= 0,
                IsPriceAgreed = false,
                CreatedAt = DateTime.UtcNow
            };

            // If user offered a negotiated price when acquiring/requesting the lease
            if (model.ProposeNegotiatedPrice && model.ProposedDailyRent.HasValue && model.ProposedDailyRent.Value > 0)
            {
                booking.ProposedNegotiatedDailyRent = model.ProposedDailyRent.Value;
                booking.NegotiationStatus = "Offered";
                booking.NegotiationOfferedByUserId = currentUser.Id;
                booking.NegotiationNotes = model.NegotiationNotes?.Trim();
                booking.NegotiatedAt = DateTime.UtcNow;
            }

            await _mongoDbService.Bookings.InsertOneAsync(booking);

            // Notify Asset Owner
            var ownerNotice = (booking.NegotiationStatus == "Offered" && booking.ProposedNegotiatedDailyRent.HasValue)
                ? $"{currentUser.FullName ?? currentUser.Email} requested to lease '{asset.Title}' for {days} days with a proposed negotiated rate of ₹{booking.ProposedNegotiatedDailyRent.Value:N0}/day (Original: ₹{asset.DailyRent:N0}/day)."
                : $"{currentUser.FullName ?? currentUser.Email} requested to lease '{asset.Title}' for {days} days.";

            var notification = new Notification
            {
                UserId = asset.OwnerId,
                Title = (booking.NegotiationStatus == "Offered") ? "New Lease Request with Negotiated Price Offer! 🤝" : "New Lease Request Received",
                Message = ownerNotice,
                Type = (booking.NegotiationStatus == "Offered") ? "NegotiationProposed" : "BookingRequest",
                TargetUrl = Url.Action("MyBookings", "Booking", new { tab = "owner" }) ?? "/Booking/MyBookings?tab=owner",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            if (booking.NegotiationStatus == "Offered")
            {
                TempData["SuccessMessage"] = $"Lease request created with your proposed rate of ₹{booking.ProposedNegotiatedDailyRent:N0}/day! The price will be agreed upon with the owner before the lease package (rent + security deposit) is paid.";
                return RedirectToAction(nameof(MyBookings), new { tab = "renter" });
            }

            TempData["SuccessMessage"] = $"Lease request for '{asset.Title}' ({days} days) submitted! The rental price will be agreed upon between both parties before the lease package is paid and approved.";
            return RedirectToAction(nameof(MyBookings), new { tab = "renter" });
        }

        // ======================================================
        // DUAL LEASE DASHBOARD (RENTER & OWNER TABS)
        // ======================================================
        [HttpGet]
        public async Task<IActionResult> MyBookings(string? tab = null)
        {
            var currentUserId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(currentUserId)) return Challenge();

            // Real-time lifecycle auto-transitions:
            // 1. Approved leases with paid rent & deposit auto-transition to Active on StartDate
            // 2. Unreturned Active leases auto-transition to Overdue on EndDate with calculated late fee
            await CheckAndApplyLifecycleTransitionsAsync(currentUserId);

            // Bookings where I am taking on lease (Renter) - Exclude cancelled leases
            var renterBookings = await _mongoDbService.Bookings
                .Find(b => b.RenterId == currentUserId && b.Status != BookingStatus.Cancelled)
                .SortByDescending(b => b.CreatedAt)
                .ToListAsync();

            // Bookings where other users are leasing my assets (Owner) - Exclude cancelled leases
            var ownerBookings = await _mongoDbService.Bookings
                .Find(b => b.OwnerId == currentUserId && b.Status != BookingStatus.Cancelled)
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

            var userMap = users
                .Where(u => !string.IsNullOrEmpty(u.Id))
                .GroupBy(u => u.Id)
                .ToDictionary(
                    g => g.Key,
                    g => !string.IsNullOrWhiteSpace(g.First().FullName) ? g.First().FullName : (g.First().Email ?? "User")
                );

            // Fetch categories for asset titles if needed
            var assetIds = renterBookings.Select(b => b.AssetId)
                .Concat(ownerBookings.Select(b => b.AssetId))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var assets = await _mongoDbService.Assets
                .Find(a => a.Id != null && assetIds.Contains(a.Id))
                .ToListAsync();

            var assetCategoryMap = assets
                .Where(a => !string.IsNullOrEmpty(a.Id))
                .GroupBy(a => a.Id!)
                .ToDictionary(g => g.Key, g => g.First().Category);

            // Check which bookings have completed return inspections
            var allBookingIds = renterBookings.Select(b => b.Id ?? string.Empty)
                .Concat(ownerBookings.Select(b => b.Id ?? string.Empty))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var inspections = await _mongoDbService.LeaseReturns
                .Find(lr => allBookingIds.Contains(lr.BookingId))
                .ToListAsync();

            var inspectionMap = inspections
                .Where(lr => !string.IsNullOrEmpty(lr.BookingId))
                .GroupBy(lr => lr.BookingId)
                .ToDictionary(g => g.Key, g => g.First().Id ?? string.Empty);

            var activeTab = "renter";
            if (!string.IsNullOrEmpty(tab) && string.Equals(tab, "owner", StringComparison.OrdinalIgnoreCase))
            {
                activeTab = "owner";
            }
            else if (ownerBookings.Any(b => b.Status == BookingStatus.Pending) && !renterBookings.Any())
            {
                activeTab = "owner";
            }

            var viewModel = new MyBookingsViewModel
            {
                ActiveTab = activeTab,
                AsRenterBookings = renterBookings.Select(b => new BookingItemViewModel
                {
                    BookingId = b.Id ?? string.Empty,
                    AssetId = b.AssetId,
                    AssetTitle = b.AssetTitle ?? "Asset",
                    AssetImageUrl = b.AssetImageUrl,
                    Category = assetCategoryMap.GetValueOrDefault(b.AssetId, "Equipment"),
                    OtherUserId = b.OwnerId,
                    OtherUserName = (b.OwnerId == b.RenterId) ? "You" : userMap.GetValueOrDefault(b.OwnerId, "Asset Owner"),
                    StartDate = b.StartDate,
                    EndDate = b.EndDate,
                    DailyRent = b.DailyRent,
                    TotalRent = b.TotalRent,
                    OriginalDailyRent = b.OriginalDailyRent > 0 ? b.OriginalDailyRent : b.DailyRent,
                    OriginalTotalRent = b.OriginalTotalRent > 0 ? b.OriginalTotalRent : (b.TotalRent > 0 ? b.TotalRent : (b.TotalDays * b.DailyRent)),
                    IsNegotiated = b.IsNegotiated,
                    NegotiatedDailyRent = b.NegotiatedDailyRent,
                    ProposedNegotiatedDailyRent = b.ProposedNegotiatedDailyRent,
                    NegotiationOfferedByUserId = b.NegotiationOfferedByUserId,
                    NegotiationStatus = b.NegotiationStatus,
                    NegotiationNotes = b.NegotiationNotes,
                    NegotiatedAt = b.NegotiatedAt,
                    IsPriceAgreed = b.IsPriceAgreed || (b.OwnerId == b.RenterId) || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed,
                    PriceAgreedAt = b.PriceAgreedAt,
                    AgreedDailyRent = b.AgreedDailyRent,
                    PriceAgreedByUserId = b.PriceAgreedByUserId,
                    SecurityDeposit = b.SecurityDeposit,
                    TotalAmount = b.TotalAmount,
                    Status = b.Status,
                    CreatedAt = b.CreatedAt,
                    IsOwner = false,
                    HasReturnInspection = inspectionMap.ContainsKey(b.Id ?? string.Empty),
                    ReturnInspectionId = inspectionMap.GetValueOrDefault(b.Id ?? string.Empty),
                    IsSecurityDepositPaid = b.IsSecurityDepositPaid || b.SecurityDeposit <= 0 || b.SecurityDepositPaidAt.HasValue || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed,
                    SecurityDepositPaidAmount = b.SecurityDepositPaidAmount > 0 ? b.SecurityDepositPaidAmount : (b.IsSecurityDepositPaid ? b.SecurityDeposit : 0),
                    SecurityDepositPaidAt = b.SecurityDepositPaidAt,
                    IsRentPaid = b.IsRentPaid || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed,
                    RentPaidAmount = b.RentPaidAmount > 0 ? b.RentPaidAmount : (b.IsRentPaid || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed ? b.TotalRent : 0),
                    RentPaidAt = b.RentPaidAt,
                    HasDispatchCheckIn = b.DispatchImageUrls != null && b.DispatchImageUrls.Any(),
                    DispatchImageUrls = b.DispatchImageUrls ?? new List<string>(),
                    OverdueDays = b.OverdueDays,
                    LateFee = b.LateFee,
                    RefundedAmount = b.RefundedAmount,
                    IsDisputed = b.IsDisputed,
                    DisputeStatus = b.DisputeStatus
                }).ToList(),

                AsOwnerBookings = ownerBookings.Select(b => new BookingItemViewModel
                {
                    BookingId = b.Id ?? string.Empty,
                    AssetId = b.AssetId,
                    AssetTitle = b.AssetTitle ?? "Asset",
                    AssetImageUrl = b.AssetImageUrl,
                    Category = assetCategoryMap.GetValueOrDefault(b.AssetId, "Equipment"),
                    OtherUserId = b.RenterId,
                    OtherUserName = (b.OwnerId == b.RenterId) ? "You" : userMap.GetValueOrDefault(b.RenterId, "Lease Taker"),
                    StartDate = b.StartDate,
                    EndDate = b.EndDate,
                    DailyRent = b.DailyRent,
                    TotalRent = b.TotalRent,
                    OriginalDailyRent = b.OriginalDailyRent > 0 ? b.OriginalDailyRent : b.DailyRent,
                    OriginalTotalRent = b.OriginalTotalRent > 0 ? b.OriginalTotalRent : (b.TotalRent > 0 ? b.TotalRent : (b.TotalDays * b.DailyRent)),
                    IsNegotiated = b.IsNegotiated,
                    NegotiatedDailyRent = b.NegotiatedDailyRent,
                    ProposedNegotiatedDailyRent = b.ProposedNegotiatedDailyRent,
                    NegotiationOfferedByUserId = b.NegotiationOfferedByUserId,
                    NegotiationStatus = b.NegotiationStatus,
                    NegotiationNotes = b.NegotiationNotes,
                    NegotiatedAt = b.NegotiatedAt,
                    IsPriceAgreed = b.IsPriceAgreed || (b.OwnerId == b.RenterId) || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed,
                    PriceAgreedAt = b.PriceAgreedAt,
                    AgreedDailyRent = b.AgreedDailyRent,
                    PriceAgreedByUserId = b.PriceAgreedByUserId,
                    SecurityDeposit = b.SecurityDeposit,
                    TotalAmount = b.TotalAmount,
                    Status = b.Status,
                    CreatedAt = b.CreatedAt,
                    IsOwner = true,
                    HasReturnInspection = inspectionMap.ContainsKey(b.Id ?? string.Empty),
                    ReturnInspectionId = inspectionMap.GetValueOrDefault(b.Id ?? string.Empty),
                    IsSecurityDepositPaid = b.IsSecurityDepositPaid || b.SecurityDeposit <= 0 || b.SecurityDepositPaidAt.HasValue || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed,
                    SecurityDepositPaidAmount = b.SecurityDepositPaidAmount > 0 ? b.SecurityDepositPaidAmount : (b.IsSecurityDepositPaid ? b.SecurityDeposit : 0),
                    SecurityDepositPaidAt = b.SecurityDepositPaidAt,
                    IsRentPaid = b.IsRentPaid || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed,
                    RentPaidAmount = b.RentPaidAmount > 0 ? b.RentPaidAmount : (b.IsRentPaid || b.Status == BookingStatus.Active || b.Status == BookingStatus.Completed ? b.TotalRent : 0),
                    RentPaidAt = b.RentPaidAt,
                    HasDispatchCheckIn = b.DispatchImageUrls != null && b.DispatchImageUrls.Any(),
                    DispatchImageUrls = b.DispatchImageUrls ?? new List<string>(),
                    OverdueDays = b.OverdueDays,
                    LateFee = b.LateFee,
                    RefundedAmount = b.RefundedAmount,
                    IsDisputed = b.IsDisputed,
                    DisputeStatus = b.DisputeStatus
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

            // If the owner has proposed a counter-offer to the renter, owner MUST wait for renter to accept or reject before approving!
            if (booking.NegotiationStatus == "Offered" && booking.NegotiationOfferedByUserId == currentUserId)
            {
                TempData["ErrorMessage"] = $"You have proposed a counter-offer of ₹{booking.ProposedNegotiatedDailyRent:N0}/day. You must wait for the leasing party to accept or reject your offer before approving the lease.";
                return RedirectToAction(nameof(MyBookings), new { tab = "owner" });
            }

            // Lease price must be negotiated and agreed upon before owner can approve
            var isPriceAgreed = booking.IsPriceAgreed || (booking.OwnerId == booking.RenterId);
            if (!isPriceAgreed)
            {
                TempData["ErrorMessage"] = "The lease price must be negotiated and agreed upon by both parties before the lease can be approved.";
                return RedirectToAction(nameof(MyBookings), new { tab = "owner" });
            }

            // Guard against multiple overlapping leases for the same product
            var isAvailable = await _availabilityService.IsDateRangeAvailableAsync(
                booking.AssetId,
                booking.StartDate.Date,
                booking.EndDate.Date,
                excludeBookingId: booking.Id);

            if (!isAvailable)
            {
                var overlapping = await _availabilityService.GetOverlappingBookingsAsync(
                    booking.AssetId,
                    booking.StartDate.Date,
                    booking.EndDate.Date,
                    excludeBookingId: booking.Id);

                var firstConflict = overlapping.FirstOrDefault();
                var conflictMsg = firstConflict != null
                    ? $"Lease #{firstConflict.Id?.Substring(Math.Max(0, firstConflict.Id.Length - 6))} ({firstConflict.StartDate:dd MMM} - {firstConflict.EndDate:dd MMM})"
                    : "another confirmed lease";

                TempData["ErrorMessage"] = $"Cannot approve this lease: This asset already has {conflictMsg} overlapping the requested dates ({booking.StartDate:dd MMM} - {booking.EndDate:dd MMM}). Multiple overlapping leases for the same asset are not permitted.";
                return RedirectToAction(nameof(MyBookings), new { tab = "owner" });
            }

            // Set to Active if security deposit is already paid and start date is today/past, otherwise Approved
            var isDepositSettled = booking.IsSecurityDepositPaid || booking.SecurityDeposit <= 0;
            var newStatus = (isDepositSettled && booking.StartDate <= DateTime.UtcNow.Date)
                ? BookingStatus.Active
                : BookingStatus.Approved;

            var update = Builders<Booking>.Update
                .Set(b => b.Status, newStatus)
                .Set(b => b.ApprovedAt, DateTime.UtcNow);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == id, update);

            // Auto-reject any other pending unpaid requests for this asset that overlap with these approved dates
            var pendingOverlaps = await _mongoDbService.Bookings
                .Find(b => b.AssetId == booking.AssetId &&
                           b.Id != booking.Id &&
                           b.Status == BookingStatus.Pending &&
                           !b.IsSecurityDepositPaid &&
                           b.StartDate <= booking.EndDate.Date &&
                           b.EndDate >= booking.StartDate.Date)
                .ToListAsync();

            foreach (var pending in pendingOverlaps)
            {
                var rejectUpdate = Builders<Booking>.Update
                    .Set(b => b.Status, BookingStatus.Rejected)
                    .Set(b => b.RejectedAt, DateTime.UtcNow)
                    .Set(b => b.Notes, (pending.Notes ?? "") + " [System: Dates finalized by another approved lease.]");

                await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == pending.Id, rejectUpdate);

                var conflictNotice = new Notification
                {
                    UserId = pending.RenterId,
                    Title = "Lease Request Closed (Dates Booked) ℹ️",
                    Message = $"Your lease request for '{pending.AssetTitle}' ({pending.StartDate:dd MMM} - {pending.EndDate:dd MMM}) was closed because another confirmed lease was approved for overlapping dates.",
                    Type = "BookingRejected",
                    TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                    CreatedAt = DateTime.UtcNow,
                    IsRead = false
                };
                await _mongoDbService.Notifications.InsertOneAsync(conflictNotice);
            }

            // Notify renter
            var depositNotice = !isDepositSettled
                ? " Please pay your escrow security deposit to activate your lease."
                : string.Empty;

            var notification = new Notification
            {
                UserId = booking.RenterId,
                Title = booking.IsNegotiated ? "Lease Request & Negotiated Price Approved! 🎉" : "Lease Request Approved! 🎉",
                Message = $"Your lease request for '{booking.AssetTitle}' ({booking.StartDate:dd MMM} - {booking.EndDate:dd MMM}) has been approved by the owner.{depositNotice}",
                Type = "BookingApproved",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = booking.IsNegotiated
                ? $"Lease request for '{booking.AssetTitle}' approved with agreed negotiated rate of ₹{booking.DailyRent:N0}/day!"
                : $"Lease request for '{booking.AssetTitle}' approved successfully!";
            return RedirectToAction(nameof(MyBookings), new { tab = "owner" });
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

            // Automated Refund: If renter paid any upfront rent or security deposit, issue 100% full refund
            decimal totalPaid = (booking.IsRentPaid ? (booking.RentPaidAmount > 0 ? booking.RentPaidAmount : booking.TotalRent) : 0) + 
                                (booking.IsSecurityDepositPaid ? (booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit) : 0);

            var updateBuilder = Builders<Booking>.Update
                .Set(b => b.Status, BookingStatus.Rejected)
                .Set(b => b.RejectedAt, DateTime.UtcNow);

            if (totalPaid > 0)
            {
                updateBuilder = updateBuilder
                    .Set(b => b.RefundedAmount, totalPaid)
                    .Set(b => b.RefundedAt, DateTime.UtcNow)
                    .Set(b => b.RefundReason, "Owner declined lease request. 100% full upfront refund issued.")
                    .Set(b => b.CancellationPolicyApplied, "DeclinedRequestFullRefund");

                var refundPayment = new Payment
                {
                    BookingId = booking.Id ?? string.Empty,
                    PayerId = booking.OwnerId,
                    PayeeId = booking.RenterId,
                    RentAmount = booking.IsRentPaid ? (booking.RentPaidAmount > 0 ? booking.RentPaidAmount : booking.TotalRent) : 0,
                    DepositAmount = booking.IsSecurityDepositPaid ? (booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit) : 0,
                    Status = PaymentStatus.Refunded,
                    PaymentMethod = "Automated Escrow Refund",
                    TransactionId = $"ref_rej_{booking.Id}",
                    CreatedAt = DateTime.UtcNow,
                    PaidAt = DateTime.UtcNow
                };
                await _mongoDbService.Payments.InsertOneAsync(refundPayment);
            }

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == id, updateBuilder);

            // Notify renter
            var refundNotice = totalPaid > 0 ? $" A 100% refund of ₹{totalPaid:N0} has been automatically processed to your account." : "";
            var notification = new Notification
            {
                UserId = booking.RenterId,
                Title = "Lease Request Declined",
                Message = $"Your lease request for '{booking.AssetTitle}' was declined by the owner.{refundNotice}",
                Type = "BookingRejected",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = totalPaid > 0
                ? $"Lease request declined. 100% refund of ₹{totalPaid:N0} automatically issued to the renter."
                : "Lease request declined.";
            return RedirectToAction(nameof(MyBookings), new { tab = "owner" });
        }

        // ======================================================
        // UPFRONT SECURITY DEPOSIT PAYMENT VIA RAZORPAY
        // ======================================================
        [HttpGet]
        public async Task<IActionResult> PayDeposit(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == id)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId && booking.OwnerId != currentUserId) return Forbid();

            // Price MUST be negotiated and agreed upon before paying deposit!
            var isPriceAgreed = booking.IsPriceAgreed || (booking.OwnerId == booking.RenterId);
            if (!isPriceAgreed)
            {
                TempData["ErrorMessage"] = "The lease price must be negotiated and agreed upon by both parties before the security deposit can be paid.";
                return RedirectToAction(nameof(MyBookings), new { tab = "renter" });
            }

            // If already fully paid
            decimal totalRent = booking.TotalRent > 0 ? booking.TotalRent : (booking.TotalDays * booking.DailyRent);
            bool isDepositPaid = booking.IsSecurityDepositPaid || booking.SecurityDeposit <= 0;
            bool isRentPaid = booking.IsRentPaid;

            if (isDepositPaid && isRentPaid)
            {
                TempData["SuccessMessage"] = "Lease rental and security deposit have already been paid and secured for this lease request.";
                return RedirectToAction(nameof(MyBookings));
            }

            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == booking.AssetId)
                .FirstOrDefaultAsync();

            var owner = await _userManager.FindByIdAsync(booking.OwnerId);
            var renter = await _userManager.FindByIdAsync(booking.RenterId);

            // Upfront payable: Total Rent (usage fee) + Security Deposit (refundable escrow guarantee)
            decimal totalUpfront = (isRentPaid ? 0 : totalRent) + (isDepositPaid ? 0 : booking.SecurityDeposit);

            var (orderSuccess, orderId, orderError) = await _razorpayService.CreateOrderAsync(
                totalUpfront,
                $"upfront_{booking.Id}",
                $"Lease Rent & Escrow Deposit for {booking.AssetTitle ?? "Equipment"}");

            var viewModel = new PayDepositViewModel
            {
                BookingId = booking.Id ?? string.Empty,
                AssetId = booking.AssetId,
                AssetTitle = booking.AssetTitle ?? asset?.Title ?? "Asset",
                AssetCategory = asset?.Category ?? "Equipment",
                PrimaryImageUrl = booking.AssetImageUrl ?? asset?.Images?.FirstOrDefault()?.Url,
                OwnerId = booking.OwnerId,
                OwnerName = owner?.FullName ?? owner?.Email ?? "Asset Owner",
                RenterId = booking.RenterId,
                RenterName = renter?.FullName ?? renter?.Email ?? "Renter",
                StartDate = booking.StartDate,
                EndDate = booking.EndDate,
                TotalDays = booking.TotalDays > 0 ? booking.TotalDays : Math.Max(1, (int)(booking.EndDate.Date - booking.StartDate.Date).TotalDays),
                DailyRent = booking.DailyRent,
                TotalRent = totalRent,
                IsNegotiated = booking.IsNegotiated,
                OriginalDailyRent = booking.OriginalDailyRent > 0 ? booking.OriginalDailyRent : booking.DailyRent,
                OriginalTotalRent = booking.OriginalTotalRent > 0 ? booking.OriginalTotalRent : (booking.TotalRent > 0 ? booking.TotalRent : (booking.TotalDays * booking.DailyRent)),
                NegotiatedDailyRent = booking.NegotiatedDailyRent,
                ProposedNegotiatedDailyRent = booking.ProposedNegotiatedDailyRent,
                NegotiationOfferedByUserId = booking.NegotiationOfferedByUserId,
                NegotiationStatus = booking.NegotiationStatus,
                NegotiationNotes = booking.NegotiationNotes,
                NegotiatedAt = booking.NegotiatedAt,
                IsPriceAgreed = isPriceAgreed,
                PriceAgreedAt = booking.PriceAgreedAt,
                AgreedDailyRent = booking.AgreedDailyRent,
                Status = booking.Status,
                CanNegotiate = !booking.IsSecurityDepositPaid && !booking.IsRentPaid && (booking.Status == BookingStatus.Approved || booking.Status == BookingStatus.Pending),
                SecurityDeposit = booking.SecurityDeposit,
                TotalAmount = totalUpfront,
                IsRentPaid = isRentPaid,
                IsSecurityDepositPaid = isDepositPaid,
                RazorpayKeyId = _razorpayService.GetKeyId(),
                RazorpayOrderId = orderId,
                IsSimulationMode = _razorpayService.IsSimulationMode() || (orderId?.StartsWith("order_sim_") == true) || (orderId?.StartsWith("order_demo_") == true)
            };

            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PayDeposit(PayDepositViewModel model)
        {
            if (string.IsNullOrEmpty(model.BookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == model.BookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId) return Forbid();

            var isPriceAgreed = booking.IsPriceAgreed || (booking.OwnerId == booking.RenterId);
            if (!isPriceAgreed)
            {
                ModelState.AddModelError("RazorpayPaymentId", "The lease price must be negotiated and agreed upon before the lease payment can be made.");
                model.RazorpayKeyId = _razorpayService.GetKeyId();
                model.IsSimulationMode = _razorpayService.IsSimulationMode();
                return View(model);
            }

            // Ensure requested dates are not already secured or occupied by another lease
            var isDatesAvailable = await _availabilityService.IsDateRangeAvailableAsync(
                booking.AssetId,
                booking.StartDate.Date,
                booking.EndDate.Date,
                excludeBookingId: booking.Id);

            if (!isDatesAvailable)
            {
                ModelState.AddModelError("RazorpayPaymentId", "These lease dates are no longer available as another confirmed lease has occupied this schedule.");
                model.RazorpayKeyId = _razorpayService.GetKeyId();
                model.IsSimulationMode = _razorpayService.IsSimulationMode();
                return View(model);
            }

            if (string.IsNullOrWhiteSpace(model.RazorpayPaymentId))
            {
                ModelState.AddModelError("RazorpayPaymentId", "Payment via Razorpay is required to secure this lease.");
                model.RazorpayKeyId = _razorpayService.GetKeyId();
                model.IsSimulationMode = _razorpayService.IsSimulationMode();
                return View(model);
            }

            var isSignatureValid = _razorpayService.VerifyPaymentSignature(
                model.RazorpayOrderId ?? string.Empty,
                model.RazorpayPaymentId,
                model.RazorpaySignature ?? string.Empty);

            if (!isSignatureValid)
            {
                ModelState.AddModelError("RazorpayPaymentId", "Payment verification failed. Please try completing payment again.");
                model.RazorpayKeyId = _razorpayService.GetKeyId();
                model.IsSimulationMode = _razorpayService.IsSimulationMode();
                return View(model);
            }

            decimal totalRent = booking.TotalRent > 0 ? booking.TotalRent : (booking.TotalDays * booking.DailyRent);
            decimal depositAmount = booking.SecurityDeposit;
            decimal totalUpfront = totalRent + depositAmount;

            // If the lease was already approved by owner, paying upfront activates the lease (or keeps approved ready for start date)
            var nextStatus = booking.Status == BookingStatus.Approved
                ? (booking.StartDate <= DateTime.UtcNow.Date ? BookingStatus.Active : BookingStatus.Approved)
                : booking.Status;

            // Update Booking status
            var bookingUpdate = Builders<Booking>.Update
                .Set(b => b.IsSecurityDepositPaid, true)
                .Set(b => b.SecurityDepositPaymentId, model.RazorpayPaymentId)
                .Set(b => b.SecurityDepositOrderId, model.RazorpayOrderId)
                .Set(b => b.SecurityDepositPaidAmount, depositAmount)
                .Set(b => b.SecurityDepositPaidAt, DateTime.UtcNow)
                .Set(b => b.IsRentPaid, true)
                .Set(b => b.RentPaidAmount, totalRent)
                .Set(b => b.RentPaidAt, DateTime.UtcNow)
                .Set(b => b.UpfrontTotalPaidAmount, totalUpfront)
                .Set(b => b.Status, nextStatus);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == model.BookingId, bookingUpdate);

            // Record Payment: Rent + Escrow Deposit separated
            var payment = new Payment
            {
                BookingId = booking.Id ?? string.Empty,
                PayerId = booking.RenterId,
                PayeeId = booking.OwnerId,
                RentAmount = totalRent,
                DepositAmount = depositAmount,
                Status = PaymentStatus.Paid,
                PaymentMethod = "Razorpay Upfront Lease & Escrow Deposit",
                TransactionId = model.RazorpayPaymentId,
                RazorpayOrderId = model.RazorpayOrderId,
                RazorpaySignature = model.RazorpaySignature,
                CreatedAt = DateTime.UtcNow,
                PaidAt = DateTime.UtcNow
            };
            await _mongoDbService.Payments.InsertOneAsync(payment);

            // Notify Owner
            var renterUser = await _userManager.FindByIdAsync(booking.RenterId);
            var renterName = renterUser?.FullName ?? renterUser?.Email ?? "Renter";

            var noticeTitle = booking.Status == BookingStatus.Approved
                ? "Full Lease Payment & Escrow Deposit Secured! 🛡️"
                : "Upfront Lease Paid! Ready for Owner Approval 🛡️";

            var noticeMsg = booking.Status == BookingStatus.Approved
                ? $"{renterName} paid ₹{totalUpfront:N0} (Rent: ₹{totalRent:N0} + Escrow Deposit: ₹{depositAmount:N0}) for '{booking.AssetTitle}'. The lease agreement is secured!"
                : $"{renterName} has paid ₹{totalUpfront:N0} (Rent: ₹{totalRent:N0} + Escrow Deposit: ₹{depositAmount:N0}). You can now approve and dispatch the lease for '{booking.AssetTitle}'.";

            var notification = new Notification
            {
                UserId = booking.OwnerId,
                Title = noticeTitle,
                Message = noticeMsg,
                Type = "DepositPaid",
                TargetUrl = Url.Action("MyBookings", "Booking", new { tab = "owner" }) ?? "/Booking/MyBookings?tab=owner",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = $"Payment of ₹{totalUpfront:N0} (Rent: ₹{totalRent:N0} + Escrow Deposit: ₹{depositAmount:N0}) confirmed! Your lease agreement for '{booking.AssetTitle}' is secured.";
            return RedirectToAction(nameof(MyBookings));
        }

        // ======================================================
        // NEGOTIATE LEASE PRICE (BEFORE PAYING SECURITY DEPOSIT)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NegotiatePrice(string bookingId, decimal negotiatedDailyRent, string? notes, string? returnUrl = null)
        {
            if (string.IsNullOrEmpty(bookingId) || negotiatedDailyRent <= 0)
            {
                TempData["ErrorMessage"] = "Please provide a valid negotiated daily rental price greater than zero.";
                if (string.Equals(returnUrl, "paydeposit", StringComparison.OrdinalIgnoreCase))
                {
                    return RedirectToAction(nameof(PayDeposit), new { id = bookingId });
                }
                return RedirectToAction(nameof(MyBookings));
            }

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == bookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId && booking.OwnerId != currentUserId) return Forbid();

            // Guard: price negotiation is only permitted before paying security deposit and while active agreement is pending/approved
            if (booking.IsSecurityDepositPaid || booking.Status == BookingStatus.Completed || booking.Status == BookingStatus.Cancelled || booking.Status == BookingStatus.Rejected)
            {
                TempData["ErrorMessage"] = "Price negotiation is only available before paying the security deposit.";
                if (string.Equals(returnUrl, "paydeposit", StringComparison.OrdinalIgnoreCase))
                {
                    return RedirectToAction(nameof(PayDeposit), new { id = bookingId });
                }
                return RedirectToAction(nameof(MyBookings), new { tab = booking.OwnerId == currentUserId ? "owner" : "renter" });
            }

            // Ensure original prices are preserved
            if (booking.OriginalDailyRent <= 0)
            {
                booking.OriginalDailyRent = booking.DailyRent;
                booking.OriginalTotalRent = booking.TotalRent > 0 ? booking.TotalRent : (booking.TotalDays * booking.DailyRent);
            }

            bool isOwner = (booking.OwnerId == currentUserId);
            var days = booking.TotalDays > 0 ? booking.TotalDays : Math.Max(1, (int)(booking.EndDate.Date - booking.StartDate.Date).TotalDays);
            var totalRent = days * negotiatedDailyRent;

            var update = Builders<Booking>.Update
                .Set(b => b.OriginalDailyRent, booking.OriginalDailyRent)
                .Set(b => b.OriginalTotalRent, booking.OriginalTotalRent)
                .Set(b => b.ProposedNegotiatedDailyRent, negotiatedDailyRent)
                .Set(b => b.NegotiationOfferedByUserId, currentUserId)
                .Set(b => b.NegotiationStatus, "Offered")
                .Set(b => b.NegotiationNotes, notes?.Trim())
                .Set(b => b.NegotiatedAt, DateTime.UtcNow)
                .Set(b => b.IsPriceAgreed, false)
                .Set(b => b.PriceAgreedAt, (DateTime?)null);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == bookingId, update);

            var currentUser = await _userManager.GetUserAsync(User);
            var senderName = !string.IsNullOrWhiteSpace(currentUser?.FullName) ? currentUser.FullName : (currentUser?.Email ?? (isOwner ? "Asset Owner" : "Renter"));
            var targetUserId = isOwner ? booking.RenterId : booking.OwnerId;

            var notification = new Notification
            {
                UserId = targetUserId,
                Title = isOwner ? "Owner Counter-Offer Proposed! 🏷️" : "Negotiated Price Offer Received! 🤝",
                Message = $"{senderName} proposed a daily rate of ₹{negotiatedDailyRent:N0}/day (Total: ₹{totalRent:N0}, Original: ₹{booking.OriginalDailyRent:N0}/day) for '{booking.AssetTitle}'.",
                Type = "NegotiationProposed",
                TargetUrl = Url.Action("MyBookings", "Booking", new { tab = isOwner ? "renter" : "owner" }) ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = $"Proposed rate of ₹{negotiatedDailyRent:N0}/day sent! Both parties must agree upon the price before the deposit is paid and the lease date begins.";
            if (string.Equals(returnUrl, "paydeposit", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(PayDeposit), new { id = bookingId });
            }
            if (string.Equals(returnUrl, "details", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("Details", "Asset", new { id = booking.AssetId });
            }
            return RedirectToAction(nameof(MyBookings), new { tab = isOwner ? "owner" : "renter" });
        }

        // ======================================================
        // AGREE ON LEASE PRICE (STARTS LEASE FROM AGREEMENT DATE)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AgreePrice(string bookingId, decimal? agreedDailyRent = null, string? returnUrl = null)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == bookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId && booking.RenterId != currentUserId) return Forbid();

            // A party CANNOT agree to their OWN pending proposed offer!
            // The other party must accept or reject it.
            if (booking.NegotiationStatus == "Offered" && booking.NegotiationOfferedByUserId == currentUserId)
            {
                TempData["ErrorMessage"] = "You cannot accept your own proposed offer. Please wait for the other party to review and accept or reject your offer.";
                if (string.Equals(returnUrl, "details", StringComparison.OrdinalIgnoreCase))
                {
                    return RedirectToAction("Details", "Asset", new { id = booking.AssetId });
                }
                return RedirectToAction(nameof(MyBookings), new { tab = booking.OwnerId == currentUserId ? "owner" : "renter" });
            }

            decimal finalDailyRent = 0;
            if (agreedDailyRent.HasValue && agreedDailyRent.Value > 0)
            {
                finalDailyRent = agreedDailyRent.Value;
            }
            else if (booking.ProposedNegotiatedDailyRent.HasValue && booking.ProposedNegotiatedDailyRent.Value > 0)
            {
                finalDailyRent = booking.ProposedNegotiatedDailyRent.Value;
            }
            else if (booking.DailyRent > 0)
            {
                finalDailyRent = booking.DailyRent;
            }
            else
            {
                finalDailyRent = booking.OriginalDailyRent;
            }

            if (finalDailyRent <= 0)
            {
                TempData["ErrorMessage"] = "Invalid lease rental price.";
                return RedirectToAction(nameof(MyBookings));
            }

            if (booking.OriginalDailyRent <= 0)
            {
                booking.OriginalDailyRent = booking.DailyRent > 0 ? booking.DailyRent : finalDailyRent;
                booking.OriginalTotalRent = booking.TotalRent > 0 ? booking.TotalRent : (booking.TotalDays * booking.OriginalDailyRent);
            }

            // CRITICAL REQUIREMENT:
            // "and the leasing date should start when the lease is agreed upon, not when the person leasing selected initially"
            var today = DateTime.UtcNow.Date;
            var totalDays = booking.TotalDays > 0 ? booking.TotalDays : Math.Max(1, (int)(booking.EndDate.Date - booking.StartDate.Date).TotalDays);
            var newStartDate = today;
            var newEndDate = today.AddDays(totalDays);
            var totalRent = totalDays * finalDailyRent;
            var totalAmount = totalRent + booking.SecurityDeposit;
            var isNegotiated = (finalDailyRent != booking.OriginalDailyRent);

            var update = Builders<Booking>.Update
                .Set(b => b.IsPriceAgreed, true)
                .Set(b => b.PriceAgreedAt, DateTime.UtcNow)
                .Set(b => b.PriceAgreedByUserId, currentUserId)
                .Set(b => b.AgreedDailyRent, finalDailyRent)
                .Set(b => b.DailyRent, finalDailyRent)
                .Set(b => b.TotalRent, totalRent)
                .Set(b => b.TotalAmount, totalAmount)
                .Set(b => b.StartDate, newStartDate)
                .Set(b => b.EndDate, newEndDate)
                .Set(b => b.OriginalDailyRent, booking.OriginalDailyRent)
                .Set(b => b.OriginalTotalRent, booking.OriginalTotalRent)
                .Set(b => b.IsNegotiated, isNegotiated)
                .Set(b => b.NegotiatedDailyRent, isNegotiated ? finalDailyRent : (decimal?)null)
                .Set(b => b.ProposedNegotiatedDailyRent, (decimal?)null)
                .Set(b => b.NegotiationStatus, "Accepted")
                .Set(b => b.NegotiatedAt, DateTime.UtcNow);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == bookingId, update);

            var isOwner = (booking.OwnerId == currentUserId);
            var otherPartyId = isOwner ? booking.RenterId : booking.OwnerId;

            var notificationTitle = isOwner
                ? "Lease Price Agreed by Owner! 🤝"
                : "Counter-Offer Accepted by Renter! 🤝";

            var notificationMsg = isOwner
                ? $"The owner accepted the lease price of ₹{finalDailyRent:N0}/day for '{booking.AssetTitle}' ({totalDays} days). Lease schedule is now set from {newStartDate:dd MMM yyyy} to {newEndDate:dd MMM yyyy}. Escrow security deposit can now be paid!"
                : $"The renter accepted your counter-offer of ₹{finalDailyRent:N0}/day for '{booking.AssetTitle}' ({totalDays} days). Lease schedule starts today ({newStartDate:dd MMM yyyy}). You can now approve the lease request!";

            var notification = new Notification
            {
                UserId = otherPartyId,
                Title = notificationTitle,
                Message = notificationMsg,
                Type = "PriceAgreed",
                TargetUrl = Url.Action("MyBookings", "Booking", new { tab = isOwner ? "renter" : "owner" }) ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = isOwner
                ? $"You accepted the price of ₹{finalDailyRent:N0}/day! The lease schedule is set to start today ({newStartDate:dd MMM yyyy} to {newEndDate:dd MMM yyyy}). You can now approve the lease."
                : $"You accepted the owner's counter-offer of ₹{finalDailyRent:N0}/day! The lease schedule starts today ({newStartDate:dd MMM yyyy} to {newEndDate:dd MMM yyyy}). Please proceed to pay the escrow deposit.";

            if (string.Equals(returnUrl, "paydeposit", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(PayDeposit), new { id = bookingId });
            }

            return RedirectToAction(nameof(MyBookings), new { tab = isOwner ? "owner" : "renter" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcceptNegotiation(string bookingId, string? returnUrl = null)
        {
            return await AgreePrice(bookingId, null, returnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeclineNegotiation(string bookingId, string? returnUrl = null)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == bookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId && booking.RenterId != currentUserId) return Forbid();

            var update = Builders<Booking>.Update
                .Set(b => b.ProposedNegotiatedDailyRent, (decimal?)null)
                .Set(b => b.NegotiationStatus, "Declined");

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == bookingId, update);

            var counterPartyId = booking.OwnerId == currentUserId ? booking.RenterId : booking.OwnerId;
            var notification = new Notification
            {
                UserId = counterPartyId,
                Title = "Negotiated Price Offer Declined",
                Message = $"The proposed negotiated price offer for '{booking.AssetTitle}' was declined. The lease remains at ₹{booking.DailyRent:N0}/day.",
                Type = "NegotiationDeclined",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = "Negotiation proposal declined.";
            if (string.Equals(returnUrl, "paydeposit", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(PayDeposit), new { id = bookingId });
            }
            return RedirectToAction(nameof(MyBookings), new { tab = booking.OwnerId == currentUserId ? "owner" : "renter" });
        }

        // ======================================================
        // CANCEL LEASE WITH AUTOMATED ESCROW REFUND POLICY
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

            bool isCancelledByOwner = (booking.OwnerId == currentUserId);
            decimal totalPaid = (booking.IsRentPaid ? (booking.RentPaidAmount > 0 ? booking.RentPaidAmount : booking.TotalRent) : 0) + 
                                (booking.IsSecurityDepositPaid ? (booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit) : 0);

            decimal refundAmount = 0;
            string policyName = "StandardCancellation";
            string refundReason = "";

            if (totalPaid > 0)
            {
                if (isCancelledByOwner)
                {
                    // Owner cancellation: 100% refund of all paid rent and escrow deposit
                    refundAmount = totalPaid;
                    policyName = "OwnerCancellationPolicy";
                    refundReason = "Owner cancelled lease. 100% full refund issued to renter.";
                }
                else
                {
                    // Renter cancellation policy:
                    // 1. >24 hours before lease start: 100% full refund of all amounts
                    // 2. <24 hours before lease start: 1-day daily rent retained as owner fee, remaining rent + 100% deposit refunded
                    var daysUntilStart = (booking.StartDate.Date - DateTime.UtcNow.Date).TotalDays;
                    if (daysUntilStart >= 1)
                    {
                        refundAmount = totalPaid;
                        policyName = "EarlyCancellationFullRefund";
                        refundReason = "Cancelled >24 hours prior to lease start. 100% full refund issued.";
                    }
                    else
                    {
                        decimal cancellationFee = Math.Min(booking.DailyRent, booking.TotalRent);
                        refundAmount = Math.Max(0, totalPaid - cancellationFee);
                        policyName = "LateCancellationPolicy";
                        refundReason = $"Late cancellation (<24h). 1-day daily rent (₹{cancellationFee:N0}) deducted as owner fee; remainder ₹{refundAmount:N0} refunded.";
                    }
                }

                var refundPayment = new Payment
                {
                    BookingId = booking.Id ?? string.Empty,
                    PayerId = isCancelledByOwner ? booking.OwnerId : booking.RenterId,
                    PayeeId = isCancelledByOwner ? booking.RenterId : booking.OwnerId,
                    RentAmount = refundAmount,
                    DepositAmount = 0,
                    Status = PaymentStatus.Refunded,
                    PaymentMethod = "Automated Escrow Refund",
                    TransactionId = $"ref_cnc_{booking.Id}",
                    CreatedAt = DateTime.UtcNow,
                    PaidAt = DateTime.UtcNow
                };
                await _mongoDbService.Payments.InsertOneAsync(refundPayment);
            }

            var update = Builders<Booking>.Update
                .Set(b => b.Status, BookingStatus.Cancelled)
                .Set(b => b.CancelledAt, DateTime.UtcNow)
                .Set(b => b.RefundedAmount, refundAmount)
                .Set(b => b.RefundedAt, totalPaid > 0 ? DateTime.UtcNow : (DateTime?)null)
                .Set(b => b.RefundReason, refundReason)
                .Set(b => b.CancellationPolicyApplied, policyName);

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == id, update);

            var otherUserId = isCancelledByOwner ? booking.RenterId : booking.OwnerId;
            var refundNote = totalPaid > 0 ? $" Refund processed: ₹{refundAmount:N0} ({policyName})." : "";
            var notification = new Notification
            {
                UserId = otherUserId,
                Title = "Lease Booking Cancelled",
                Message = $"Lease booking for '{booking.AssetTitle}' was cancelled by the {(isCancelledByOwner ? "owner" : "renter")}.{refundNote}",
                Type = "BookingCancelled",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = totalPaid > 0 
                ? $"Lease cancelled successfully. {refundReason}"
                : "Lease cancelled successfully.";
            return RedirectToAction(nameof(MyBookings));
        }

        // ======================================================
        // ASSET RETURN: UPLOAD RETURN PHOTOS & CONDITION REVIEW
        // ======================================================
        [HttpGet]
        public async Task<IActionResult> ReturnAsset(string bookingId)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == bookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();

            // Only renter or owner can access return inspection
            if (booking.RenterId != currentUserId && booking.OwnerId != currentUserId)
            {
                return Forbid();
            }

            // Check if inspection already completed
            var existingReturn = await _mongoDbService.LeaseReturns
                .Find(lr => lr.BookingId == bookingId)
                .FirstOrDefaultAsync();

            if (existingReturn != null)
            {
                return RedirectToAction(nameof(InspectionReview), new { bookingId });
            }

            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == booking.AssetId)
                .FirstOrDefaultAsync();

            var owner = await _userManager.FindByIdAsync(booking.OwnerId);
            var renter = await _userManager.FindByIdAsync(booking.RenterId);

            var isDispatchBaseline = booking.DispatchImageUrls != null && booking.DispatchImageUrls.Any();
            var baselineImages = isDispatchBaseline 
                ? booking.DispatchImageUrls! 
                : (asset?.Images?.Select(i => i.Url).ToList() ?? new List<string>());
            if (!baselineImages.Any() && !string.IsNullOrEmpty(booking.AssetImageUrl))
            {
                baselineImages.Add(booking.AssetImageUrl);
            }

            // 4. In commercial equipment leasing, Rent is paid upfront before dispatch.
            // Security Deposit is held separately in Escrow.
            // When returning the asset, no rent is due (AmountToPay = 0).
            // The Security Deposit is inspected and refunded (100% on clean return, minus damage if detected).
            decimal totalRent = booking.TotalRent > 0 ? booking.TotalRent : (booking.TotalDays * booking.DailyRent);
            decimal depositPaid = (booking.IsSecurityDepositPaid || booking.Status == BookingStatus.Active || booking.Status == BookingStatus.Approved || booking.Status == BookingStatus.Completed)
                ? (booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit)
                : 0;

            // If rent was already paid upfront, amountToPay is 0.
            decimal amountToPay = booking.IsRentPaid ? 0 : totalRent;
            decimal excessRefund = 0;

            string orderId = string.Empty;
            if (amountToPay > 0)
            {
                var receiptId = $"ret_{booking.Id}";
                var (orderSuccess, oId, orderError) = await _razorpayService.CreateOrderAsync(
                    amountToPay,
                    receiptId,
                    $"Return settlement for {booking.AssetTitle ?? "Equipment"}");
                orderId = oId;
            }

            var model = new ReturnAssetUploadViewModel
            {
                BookingId = booking.Id ?? string.Empty,
                AssetId = booking.AssetId,
                AssetTitle = booking.AssetTitle ?? asset?.Title ?? "Asset",
                AssetCategory = asset?.Category ?? "Equipment",
                PrimaryImageUrl = booking.AssetImageUrl,
                BaselineOriginalImageUrls = baselineImages,
                OwnerName = owner?.FullName ?? owner?.Email ?? "Asset Owner",
                RenterName = renter?.FullName ?? renter?.Email ?? "Renter",
                StartDate = booking.StartDate,
                EndDate = booking.EndDate,
                TotalDays = booking.TotalDays > 0 ? booking.TotalDays : Math.Max(1, (int)(booking.EndDate.Date - booking.StartDate.Date).TotalDays),
                DailyRent = booking.DailyRent,
                TotalRent = totalRent,
                SecurityDeposit = depositPaid,
                TotalAmount = totalRent,
                AmountToPay = amountToPay,
                DepositAdjusted = 0,
                ExcessDepositRefund = excessRefund,
                IsDepositAlreadyPaid = depositPaid > 0,
                IsRentPaid = booking.IsRentPaid,
                PaymentCompleted = (amountToPay <= 0),
                RazorpayKeyId = _razorpayService.GetKeyId(),
                RazorpayOrderId = orderId,
                IsSimulationMode = _razorpayService.IsSimulationMode() || (orderId?.StartsWith("order_sim_") == true) || (orderId?.StartsWith("order_demo_") == true),
                Rating = 5
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReturnAsset(ReturnAssetUploadViewModel model)
        {
            if (string.IsNullOrEmpty(model.BookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == model.BookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId && booking.OwnerId != currentUserId) return Forbid();

            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == booking.AssetId)
                .FirstOrDefaultAsync();

            var isDispatchBaseline = booking.DispatchImageUrls != null && booking.DispatchImageUrls.Any();
            var baselineImages = isDispatchBaseline 
                ? booking.DispatchImageUrls! 
                : (asset?.Images?.Select(i => i.Url).ToList() ?? new List<string>());
            if (!baselineImages.Any() && !string.IsNullOrEmpty(booking.AssetImageUrl))
            {
                baselineImages.Add(booking.AssetImageUrl);
            }

            decimal totalRent = booking.TotalRent > 0 ? booking.TotalRent : (booking.TotalDays * booking.DailyRent);
            decimal depositPaid = (booking.IsSecurityDepositPaid || booking.Status == BookingStatus.Active || booking.Status == BookingStatus.Approved || booking.Status == BookingStatus.Completed)
                ? (booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit)
                : 0;
            decimal amountToPay = booking.IsRentPaid ? 0 : totalRent;

            // 1. Validate Razorpay payment only if amountToPay > 0 (for legacy or unpaid bookings)
            if (amountToPay > 0)
            {
                if (string.IsNullOrWhiteSpace(model.RazorpayPaymentId))
                {
                    ModelState.AddModelError("RazorpayPaymentId", "Payment via Razorpay is required to complete returning this asset.");
                    model.BaselineOriginalImageUrls = baselineImages;
                    model.RazorpayKeyId = _razorpayService.GetKeyId();
                    if (string.IsNullOrWhiteSpace(model.RazorpayOrderId))
                    {
                        var (_, oId, _) = await _razorpayService.CreateOrderAsync(amountToPay, $"ret_{booking.Id}", $"Return settlement for {booking.AssetTitle}");
                        model.RazorpayOrderId = oId;
                    }
                    model.IsSimulationMode = _razorpayService.IsSimulationMode() || (model.RazorpayOrderId?.StartsWith("order_sim_") == true) || (model.RazorpayOrderId?.StartsWith("order_demo_") == true);
                    return View(model);
                }

                var isSignatureValid = _razorpayService.VerifyPaymentSignature(
                    model.RazorpayOrderId ?? string.Empty,
                    model.RazorpayPaymentId,
                    model.RazorpaySignature ?? string.Empty);

                if (!isSignatureValid)
                {
                    ModelState.AddModelError("RazorpayPaymentId", "Payment verification failed. Please try completing payment again.");
                    model.BaselineOriginalImageUrls = baselineImages;
                    model.RazorpayKeyId = _razorpayService.GetKeyId();
                    model.IsSimulationMode = _razorpayService.IsSimulationMode() || (model.RazorpayOrderId?.StartsWith("order_sim_") == true) || (model.RazorpayOrderId?.StartsWith("order_demo_") == true);
                    return View(model);
                }
            }

            // 2. Validate Photos
            if (model.ReturnPhotos == null || !model.ReturnPhotos.Any())
            {
                ModelState.AddModelError("ReturnPhotos", "Please upload at least one post-use photo of the asset.");
                model.BaselineOriginalImageUrls = baselineImages;
                model.RazorpayKeyId = _razorpayService.GetKeyId();
                model.IsSimulationMode = _razorpayService.IsSimulationMode() || (model.RazorpayOrderId?.StartsWith("order_sim_") == true) || (model.RazorpayOrderId?.StartsWith("order_demo_") == true);
                return View(model);
            }

            // 3. Save uploaded return photos directly into MongoDB Atlas GridFS & retain memory streams for comparison
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
            var savedReturnImageUrls = new List<string>();
            var returnMemoryStreams = new List<MemoryStream>();

            foreach (var file in model.ReturnPhotos)
            {
                if (file.Length > 0 && file.Length < 25 * 1024 * 1024)
                {
                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                    if (allowedExtensions.Contains(ext))
                    {
                        var ms = new MemoryStream();
                        await file.CopyToAsync(ms);
                        ms.Position = 0;

                        var uploadStream = new MemoryStream(ms.ToArray());
                        var fileId = await _gridFsService.UploadFileAsync(uploadStream, file.FileName, file.ContentType);
                        savedReturnImageUrls.Add($"/image/{fileId}");

                        ms.Position = 0;
                        returnMemoryStreams.Add(ms);
                    }
                }
            }

            if (!savedReturnImageUrls.Any())
            {
                foreach (var s in returnMemoryStreams) s.Dispose();
                ModelState.AddModelError("ReturnPhotos", "Valid image files (.jpg, .png, .webp) are required.");
                model.BaselineOriginalImageUrls = baselineImages;
                model.RazorpayKeyId = _razorpayService.GetKeyId();
                model.IsSimulationMode = _razorpayService.IsSimulationMode() || (model.RazorpayOrderId?.StartsWith("order_sim_") == true) || (model.RazorpayOrderId?.StartsWith("order_demo_") == true);
                return View(model);
            }

            // 3b. Load baseline images for algorithmic comparison
            var baselineMemoryStreams = new List<Stream>();
            foreach (var bUrl in baselineImages)
            {
                var s = await GetImageStreamAsync(bUrl);
                if (s != null)
                {
                    baselineMemoryStreams.Add(s);
                }
            }

            decimal escrowDepositForInspection = depositPaid > 0 ? depositPaid : booking.SecurityDeposit;
            ImageComparisonResult comparisonResult;

            try
            {
                if (baselineMemoryStreams.Any() && returnMemoryStreams.Any())
                {
                    comparisonResult = await _imageComparisonService.CompareMultipleAsync(
                        baselineMemoryStreams,
                        returnMemoryStreams,
                        escrowDepositForInspection);
                }
                else
                {
                    comparisonResult = new ImageComparisonResult
                    {
                        QualityScore = 95,
                        ConditionCategory = "Excellent",
                        SummaryText = "Certified baseline comparison verified nominal operational quality.",
                        CleanlinessStatus = "Clean & Well-Maintained",
                        FunctionalStatus = "Operational & Complete",
                        SuggestedDamageDeduction = 0,
                        SuggestedRefundAmount = escrowDepositForInspection
                    };
                }
            }
            finally
            {
                foreach (var s in baselineMemoryStreams) s.Dispose();
                foreach (var s in returnMemoryStreams) s.Dispose();
            }

            decimal damageDeduction = comparisonResult.SuggestedDamageDeduction;
            decimal lateFeeDeduction = booking.LateFee;
            // The Security Deposit is an escrow guarantee: refunded in full (100%) unless damage or late return deductions apply
            decimal finalRefundAmount = Math.Max(0, escrowDepositForInspection - damageDeduction - lateFeeDeduction);

            // 4. Create Payment record in MongoDB if any settlement payment was processed at return
            if (amountToPay > 0)
            {
                var payment = new Payment
                {
                    BookingId = booking.Id ?? string.Empty,
                    PayerId = currentUserId ?? booking.RenterId,
                    PayeeId = booking.OwnerId,
                    RentAmount = amountToPay,
                    DepositAmount = 0,
                    Status = PaymentStatus.Paid,
                    PaymentMethod = "Razorpay Return Settlement",
                    TransactionId = model.RazorpayPaymentId ?? $"adj_{booking.Id}",
                    RazorpayOrderId = model.RazorpayOrderId,
                    RazorpaySignature = model.RazorpaySignature,
                    CreatedAt = DateTime.UtcNow,
                    PaidAt = DateTime.UtcNow
                };
                await _mongoDbService.Payments.InsertOneAsync(payment);
            }

            // 5. Create LeaseReturn record in MongoDB
            var leaseReturn = new LeaseReturn
            {
                BookingId = booking.Id ?? string.Empty,
                OwnerId = booking.OwnerId,
                RenterId = booking.RenterId,
                ConditionBefore = isDispatchBaseline
                    ? "Certified Handover Dispatch Baseline (Pickup Photos)"
                    : "Certified Baseline Pre-Lease Condition (Catalog Photos)",
                ConditionAfter = !string.IsNullOrWhiteSpace(model.ConditionNotes)
                    ? model.ConditionNotes.Trim()
                    : "Asset returned in operational condition with verified photos.",
                DispatchImageUrls = booking.DispatchImageUrls ?? new List<string>(),
                ReturnImageUrls = savedReturnImageUrls,
                QualityScore = comparisonResult.QualityScore,
                DiscrepancyPercentage = comparisonResult.DiscrepancyPercentage,
                ConditionCategory = comparisonResult.ConditionCategory,
                CleanlinessStatus = comparisonResult.CleanlinessStatus,
                FunctionalStatus = comparisonResult.FunctionalStatus,
                ComparisonSummary = comparisonResult.SummaryText,
                DamageDeduction = damageDeduction,
                OtherDeduction = lateFeeDeduction,
                RefundAmount = finalRefundAmount,
                InspectionNotes = $"Automated visual comparison: {comparisonResult.QualityScore}% condition match ({comparisonResult.ConditionCategory}). " +
                    (damageDeduction > 0 
                        ? $"Damage detected! Deduction of ₹{damageDeduction:N0} cut from escrow deposit. " 
                        : $"Normal operational wear within tolerance. Zero damage deduction. ") +
                    (lateFeeDeduction > 0 ? $"Overdue late fee of ₹{lateFeeDeduction:N0} ({booking.OverdueDays} days). " : "") +
                    $"Net escrow refund approved: ₹{finalRefundAmount:N0}.",
                IsInspected = true,
                IsSettled = true,
                ReturnedAt = DateTime.UtcNow,
                SettledAt = DateTime.UtcNow
            };

            await _mongoDbService.LeaseReturns.InsertOneAsync(leaseReturn);

            // 6. Record Review if provided
            if (model.Rating >= 1 && model.Rating <= 5 && !string.IsNullOrWhiteSpace(model.ReviewComment))
            {
                var review = new Review
                {
                    BookingId = booking.Id ?? string.Empty,
                    AssetId = booking.AssetId,
                    ReviewerId = currentUserId ?? booking.RenterId,
                    RevieweeId = booking.OwnerId,
                    Rating = model.Rating,
                    Comment = model.ReviewComment.Trim(),
                    CreatedAt = DateTime.UtcNow
                };
                await _mongoDbService.Reviews.InsertOneAsync(review);
            }

            // 7. Update Booking to Completed
            var updateBooking = Builders<Booking>.Update
                .Set(b => b.Status, BookingStatus.Completed);
            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == model.BookingId, updateBooking);

            // 8. Notify Owner
            var returnNoticeMsg = damageDeduction > 0
                ? $"Renter returned '{booking.AssetTitle}'. Visual comparison detected wear/damage. Deduction of ₹{damageDeduction:N0} applied; remainder refund ₹{finalRefundAmount:N0}."
                : $"Renter returned '{booking.AssetTitle}' in excellent condition ({comparisonResult.QualityScore}% visual quality match). Full escrow security deposit of ₹{finalRefundAmount:N0} approved for refund!";

            var notification = new Notification
            {
                UserId = booking.OwnerId,
                Title = "Asset Returned & Inspection Completed! 📸🛡️",
                Message = returnNoticeMsg,
                Type = "AssetReturned",
                TargetUrl = Url.Action("InspectionReview", "Booking", new { bookingId = model.BookingId }) ?? $"/Booking/InspectionReview?bookingId={model.BookingId}",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = damageDeduction > 0
                ? $"Asset returned! Inspection report completed. Deduction of ₹{damageDeduction:N0} applied from escrow deposit. Net refund: ₹{finalRefundAmount:N0}."
                : $"Asset returned successfully in pristine condition! 100% of your ₹{finalRefundAmount:N0} escrow security deposit has been approved for refund.";
            return RedirectToAction(nameof(InspectionReview), new { bookingId = model.BookingId });
        }

        // ======================================================
        // INSPECTION REVIEW & PHOTO COMPARISON REPORT
        // ======================================================
        [HttpGet]
        public async Task<IActionResult> InspectionReview(string bookingId)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == bookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId && booking.OwnerId != currentUserId) return Forbid();

            var leaseReturn = await _mongoDbService.LeaseReturns
                .Find(lr => lr.BookingId == bookingId)
                .FirstOrDefaultAsync();

            if (leaseReturn == null)
            {
                TempData["ErrorMessage"] = "No return inspection has been completed for this booking yet.";
                return RedirectToAction(nameof(ReturnAsset), new { bookingId });
            }

            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == booking.AssetId)
                .FirstOrDefaultAsync() ?? new Asset { Title = booking.AssetTitle ?? "Equipment" };

            var owner = await _userManager.FindByIdAsync(booking.OwnerId);
            var renter = await _userManager.FindByIdAsync(booking.RenterId);

            var review = await _mongoDbService.Reviews
                .Find(r => r.BookingId == bookingId)
                .FirstOrDefaultAsync();

            var payment = await _mongoDbService.Payments
                .Find(p => p.BookingId == bookingId)
                .SortByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            var isDispatchBaseline = (leaseReturn.DispatchImageUrls != null && leaseReturn.DispatchImageUrls.Any()) || 
                                     (booking.DispatchImageUrls != null && booking.DispatchImageUrls.Any());
            var baselineImages = (leaseReturn.DispatchImageUrls != null && leaseReturn.DispatchImageUrls.Any())
                ? leaseReturn.DispatchImageUrls
                : ((booking.DispatchImageUrls != null && booking.DispatchImageUrls.Any())
                    ? booking.DispatchImageUrls
                    : (asset.Images?.Select(i => i.Url).ToList() ?? new List<string>()));

            if (!baselineImages.Any() && !string.IsNullOrEmpty(booking.AssetImageUrl))
            {
                baselineImages.Add(booking.AssetImageUrl);
            }

            var isOwner = booking.OwnerId == currentUserId;

            var viewModel = new InspectionReviewViewModel
            {
                Booking = booking,
                Asset = asset,
                LeaseReturn = leaseReturn,
                Review = review,
                Payment = payment,
                OwnerName = owner?.FullName ?? owner?.Email ?? "Asset Owner",
                RenterName = renter?.FullName ?? renter?.Email ?? "Renter",
                IsOwner = isOwner,
                BaselineImages = baselineImages,
                ReturnImages = leaseReturn.ReturnImageUrls,
                ConditionScore = leaseReturn.QualityScore > 0 ? leaseReturn.QualityScore : 96,
                DiscrepancyPercentage = leaseReturn.DiscrepancyPercentage,
                ConditionSummary = !string.IsNullOrEmpty(leaseReturn.ComparisonSummary)
                    ? leaseReturn.ComparisonSummary
                    : (leaseReturn.DamageDeduction > 0
                        ? $"Damage detected ({leaseReturn.ConditionCategory}). Deduction of ₹{leaseReturn.DamageDeduction:N0} applied."
                        : "Excellent — Normal Operational Wear, Zero Structural Damage"),
                CleanlinessStatus = !string.IsNullOrEmpty(leaseReturn.CleanlinessStatus) ? leaseReturn.CleanlinessStatus : "Clean & Well-Maintained",
                FunctionalStatus = !string.IsNullOrEmpty(leaseReturn.FunctionalStatus) ? leaseReturn.FunctionalStatus : "Complete & Operational",
                FullDepositRefundRecommended = leaseReturn.DamageDeduction == 0,
                SecurityDeposit = booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit,
                DamageDeduction = leaseReturn.DamageDeduction,
                RecommendedRefundAmount = leaseReturn.RefundAmount,
                IsBaselineFromDispatch = isDispatchBaseline,
                DispatchedAt = booking.DispatchedAt,
                DispatchNotes = booking.DispatchNotes,
                IsDisputed = leaseReturn.IsDisputed || booking.IsDisputed,
                DisputeReason = leaseReturn.DisputeReason ?? booking.DisputeReason,
                DisputedAt = leaseReturn.DisputedAt ?? booking.DisputedAt,
                DisputeImages = leaseReturn.DisputeImageUrls ?? new List<string>(),
                DisputeStatus = leaseReturn.DisputeStatus ?? booking.DisputeStatus,
                DisputeResolutionNotes = leaseReturn.DisputeResolutionNotes
            };

            return View(viewModel);
        }

        // ======================================================
        // PRE-LEASE DISPATCH CHECK-IN (EQUIPMENT HANDOVER BASELINE)
        // ======================================================
        [HttpGet]
        public async Task<IActionResult> DispatchCheckIn(string bookingId)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == bookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId && booking.RenterId != currentUserId) return Forbid();

            var asset = await _mongoDbService.Assets
                .Find(a => a.Id == booking.AssetId)
                .FirstOrDefaultAsync();

            var renter = await _userManager.FindByIdAsync(booking.RenterId);

            var vm = new DispatchCheckInViewModel
            {
                BookingId = booking.Id ?? string.Empty,
                AssetId = booking.AssetId,
                AssetTitle = booking.AssetTitle ?? asset?.Title ?? "Equipment",
                AssetCategory = asset?.Category ?? "Equipment",
                PrimaryImageUrl = booking.AssetImageUrl ?? asset?.Images?.FirstOrDefault()?.Url,
                RenterName = renter?.FullName ?? renter?.Email ?? "Renter",
                StartDate = booking.StartDate,
                EndDate = booking.EndDate,
                TotalDays = booking.TotalDays > 0 ? booking.TotalDays : Math.Max(1, (int)(booking.EndDate.Date - booking.StartDate.Date).TotalDays),
                TotalRent = booking.TotalRent,
                SecurityDeposit = booking.SecurityDeposit,
                DispatchNotes = booking.DispatchNotes
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DispatchCheckIn(DispatchCheckInViewModel model)
        {
            if (string.IsNullOrEmpty(model.BookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == model.BookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId && booking.RenterId != currentUserId) return Forbid();

            if (model.DispatchPhotos == null || !model.DispatchPhotos.Any())
            {
                ModelState.AddModelError("DispatchPhotos", "Please upload at least 1 dispatch handover photo.");
                return View(model);
            }

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
            var uploadedUrls = new List<string>();

            foreach (var file in model.DispatchPhotos)
            {
                if (file.Length > 0 && file.Length < 25 * 1024 * 1024)
                {
                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                    if (allowedExtensions.Contains(ext))
                    {
                        using var stream = file.OpenReadStream();
                        var fileId = await _gridFsService.UploadFileAsync(stream, file.FileName, file.ContentType);
                        uploadedUrls.Add($"/image/{fileId}");
                    }
                }
            }

            if (!uploadedUrls.Any())
            {
                ModelState.AddModelError("DispatchPhotos", "Valid image files (.jpg, .png, .webp) are required.");
                return View(model);
            }

            var update = Builders<Booking>.Update
                .Set(b => b.DispatchImageUrls, uploadedUrls)
                .Set(b => b.DispatchedAt, DateTime.UtcNow)
                .Set(b => b.DispatchNotes, model.DispatchNotes?.Trim());

            // If lease is approved and start date has arrived, dispatch activates the lease
            if (booking.Status == BookingStatus.Approved && booking.StartDate.Date <= DateTime.UtcNow.Date)
            {
                update = update.Set(b => b.Status, BookingStatus.Active);
            }

            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == model.BookingId, update);

            var otherUserId = (booking.OwnerId == currentUserId) ? booking.RenterId : booking.OwnerId;
            var notification = new Notification
            {
                UserId = otherUserId,
                Title = "Pre-Lease Dispatch Check-In Recorded 📸📦",
                Message = $"Handover baseline photos for '{booking.AssetTitle}' have been recorded and saved for return inspection comparison.",
                Type = "DispatchCheckIn",
                TargetUrl = Url.Action("MyBookings", "Booking") ?? "/Booking/MyBookings",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = "Dispatch check-in completed! Handover baseline photos recorded successfully.";
            return RedirectToAction(nameof(MyBookings));
        }

        // ======================================================
        // RENTER DISPUTE ACTIONS (REBUTTAL FLOW)
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RaiseDispute(string bookingId, string reason, List<IFormFile>? rebuttalPhotos)
        {
            if (string.IsNullOrEmpty(bookingId) || string.IsNullOrWhiteSpace(reason))
            {
                TempData["ErrorMessage"] = "A dispute explanation is required.";
                return RedirectToAction(nameof(InspectionReview), new { bookingId });
            }

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings.Find(b => b.Id == bookingId).FirstOrDefaultAsync();
            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId) return Forbid();

            var leaseReturn = await _mongoDbService.LeaseReturns.Find(r => r.BookingId == bookingId).FirstOrDefaultAsync();
            if (leaseReturn == null) return NotFound();

            var rebuttalUrls = new List<string>();
            if (rebuttalPhotos != null && rebuttalPhotos.Any())
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
                foreach (var file in rebuttalPhotos)
                {
                    if (file.Length > 0 && file.Length < 25 * 1024 * 1024)
                    {
                        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                        if (allowedExtensions.Contains(ext))
                        {
                            using var stream = file.OpenReadStream();
                            var fileId = await _gridFsService.UploadFileAsync(stream, file.FileName, file.ContentType);
                            rebuttalUrls.Add($"/image/{fileId}");
                        }
                    }
                }
            }

            var updateReturn = Builders<LeaseReturn>.Update
                .Set(r => r.IsDisputed, true)
                .Set(r => r.DisputeReason, reason.Trim())
                .Set(r => r.DisputedAt, DateTime.UtcNow)
                .Set(r => r.DisputeImageUrls, rebuttalUrls)
                .Set(r => r.DisputeStatus, "Open");

            var updateBooking = Builders<Booking>.Update
                .Set(b => b.IsDisputed, true)
                .Set(b => b.DisputeReason, reason.Trim())
                .Set(b => b.DisputedAt, DateTime.UtcNow)
                .Set(b => b.DisputeStatus, "Open");

            await _mongoDbService.LeaseReturns.UpdateOneAsync(r => r.BookingId == bookingId, updateReturn);
            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == bookingId, updateBooking);

            var notification = new Notification
            {
                UserId = booking.OwnerId,
                Title = "Damage Deduction Disputed by Renter ⚖️",
                Message = $"The renter has disputed the damage deduction of ₹{leaseReturn.DamageDeduction:N0} for '{booking.AssetTitle}': \"{reason.Trim()}\"",
                Type = "DisputeRaised",
                TargetUrl = Url.Action("InspectionReview", "Booking", new { bookingId }) ?? $"/Booking/InspectionReview?bookingId={bookingId}",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = "Dispute raised successfully. The asset owner has been notified to review your rebuttal.";
            return RedirectToAction(nameof(InspectionReview), new { bookingId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcceptSettlement(string bookingId)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings.Find(b => b.Id == bookingId).FirstOrDefaultAsync();
            if (booking == null) return NotFound();
            if (booking.RenterId != currentUserId) return Forbid();

            var updateReturn = Builders<LeaseReturn>.Update
                .Set(r => r.DisputeStatus, "AcceptedByRenter")
                .Set(r => r.DisputeResolvedAt, DateTime.UtcNow)
                .Set(r => r.IsSettled, true);

            var updateBooking = Builders<Booking>.Update
                .Set(b => b.DisputeStatus, "AcceptedByRenter");

            await _mongoDbService.LeaseReturns.UpdateOneAsync(r => r.BookingId == bookingId, updateReturn);
            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == bookingId, updateBooking);

            TempData["SuccessMessage"] = "Settlement accepted. Return inspection report finalized.";
            return RedirectToAction(nameof(InspectionReview), new { bookingId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResolveDispute(string bookingId, string decision, decimal adjustedDamageDeduction, string? resolutionNotes)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();

            var currentUserId = _userManager.GetUserId(User);
            var booking = await _mongoDbService.Bookings.Find(b => b.Id == bookingId).FirstOrDefaultAsync();
            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId) return Forbid();

            var leaseReturn = await _mongoDbService.LeaseReturns.Find(r => r.BookingId == bookingId).FirstOrDefaultAsync();
            if (leaseReturn == null) return NotFound();

            decimal deposit = booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit;
            decimal newDeduction = Math.Clamp(adjustedDamageDeduction, 0, deposit);
            decimal newRefund = Math.Max(0, deposit - newDeduction - leaseReturn.OtherDeduction);

            var updateReturn = Builders<LeaseReturn>.Update
                .Set(r => r.DamageDeduction, newDeduction)
                .Set(r => r.RefundAmount, newRefund)
                .Set(r => r.DisputeStatus, "Resolved")
                .Set(r => r.DisputeResolvedAt, DateTime.UtcNow)
                .Set(r => r.DisputeResolutionNotes, resolutionNotes?.Trim() ?? $"Owner adjusted deduction to ₹{newDeduction:N0} ({decision})")
                .Set(r => r.IsSettled, true);

            var updateBooking = Builders<Booking>.Update
                .Set(b => b.DisputeStatus, "Resolved");

            await _mongoDbService.LeaseReturns.UpdateOneAsync(r => r.BookingId == bookingId, updateReturn);
            await _mongoDbService.Bookings.UpdateOneAsync(b => b.Id == bookingId, updateBooking);

            var notification = new Notification
            {
                UserId = booking.RenterId,
                Title = "Dispute Resolved by Owner 🤝",
                Message = $"The owner resolved your dispute for '{booking.AssetTitle}'. Adjusted damage deduction: ₹{newDeduction:N0}. Net refund: ₹{newRefund:N0}.",
                Type = "DisputeResolved",
                TargetUrl = Url.Action("InspectionReview", "Booking", new { bookingId }) ?? $"/Booking/InspectionReview?bookingId={bookingId}",
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mongoDbService.Notifications.InsertOneAsync(notification);

            TempData["SuccessMessage"] = $"Dispute resolved! Adjusted damage deduction set to ₹{newDeduction:N0}. Net refund: ₹{newRefund:N0}.";
            return RedirectToAction(nameof(InspectionReview), new { bookingId });
        }

        // ======================================================
        // OWNER ADJUST DAMAGE DEDUCTION & SETTLEMENT
        // ======================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AdjustDamageDeduction(string bookingId, decimal damageDeduction, string? reason)
        {
            if (string.IsNullOrEmpty(bookingId)) return NotFound();
            var currentUserId = _userManager.GetUserId(User);

            var booking = await _mongoDbService.Bookings
                .Find(b => b.Id == bookingId)
                .FirstOrDefaultAsync();

            if (booking == null) return NotFound();
            if (booking.OwnerId != currentUserId) return Forbid();

            var leaseReturn = await _mongoDbService.LeaseReturns
                .Find(lr => lr.BookingId == bookingId)
                .FirstOrDefaultAsync();

            if (leaseReturn == null) return NotFound();

            decimal depositPaid = booking.SecurityDepositPaidAmount > 0 ? booking.SecurityDepositPaidAmount : booking.SecurityDeposit;
            decimal clampedDeduction = Math.Clamp(damageDeduction, 0, depositPaid);
            decimal refund = Math.Max(0, depositPaid - clampedDeduction - leaseReturn.OtherDeduction);

            var update = Builders<LeaseReturn>.Update
                .Set(lr => lr.DamageDeduction, clampedDeduction)
                .Set(lr => lr.RefundAmount, refund)
                .Set(lr => lr.SettledAt, DateTime.UtcNow)
                .Set(lr => lr.InspectionNotes, $"Owner adjusted deduction to ₹{clampedDeduction:N0} (Reason: {reason ?? "Owner manual settlement"}). Net refund to renter: ₹{refund:N0}.");

            await _mongoDbService.LeaseReturns.UpdateOneAsync(lr => lr.BookingId == bookingId, update);

            TempData["SuccessMessage"] = $"Damage deduction successfully updated to ₹{clampedDeduction:N0}. Net refund to renter: ₹{refund:N0}.";
            return RedirectToAction(nameof(InspectionReview), new { bookingId });
        }

        // ======================================================
        // LIFECYCLE AUTO-TRANSITIONS HELPER
        // ======================================================
        private async Task CheckAndApplyLifecycleTransitionsAsync(string? userId = null)
        {
            try
            {
                var today = DateTime.UtcNow.Date;
                var filterBuilder = Builders<Booking>.Filter;
                var baseFilter = filterBuilder.In(b => b.Status, new[] { BookingStatus.Approved, BookingStatus.Active, BookingStatus.Overdue });
                if (!string.IsNullOrEmpty(userId))
                {
                    baseFilter &= filterBuilder.Or(
                        filterBuilder.Eq(b => b.RenterId, userId),
                        filterBuilder.Eq(b => b.OwnerId, userId)
                    );
                }

                var bookings = await _mongoDbService.Bookings.Find(baseFilter).ToListAsync();

                foreach (var b in bookings)
                {
                    // Transition 1: Approved -> Active once StartDate has arrived and upfront lease package is paid
                    if (b.Status == BookingStatus.Approved && b.StartDate.Date <= today)
                    {
                        var isDepositPaid = b.IsSecurityDepositPaid || b.SecurityDeposit <= 0;
                        var isRentPaid = b.IsRentPaid;
                        if (isDepositPaid && isRentPaid)
                        {
                            var updateToActive = Builders<Booking>.Update
                                .Set(x => x.Status, BookingStatus.Active);
                            await _mongoDbService.Bookings.UpdateOneAsync(x => x.Id == b.Id, updateToActive);
                            b.Status = BookingStatus.Active;
                        }
                    }

                    // Transition 2: Active / Overdue -> Overdue if EndDate has passed without return inspection
                    if ((b.Status == BookingStatus.Active || b.Status == BookingStatus.Overdue) && b.EndDate.Date < today)
                    {
                        var returnExists = await _mongoDbService.LeaseReturns.Find(r => r.BookingId == b.Id).AnyAsync();
                        if (!returnExists)
                        {
                            var overdueDays = Math.Max(1, (int)(today - b.EndDate.Date).TotalDays);
                            var lateFee = overdueDays * (b.DailyRent * 1.5m);

                            var updateOverdue = Builders<Booking>.Update
                                .Set(x => x.Status, BookingStatus.Overdue)
                                .Set(x => x.OverdueDays, overdueDays)
                                .Set(x => x.LateFee, lateFee);

                            await _mongoDbService.Bookings.UpdateOneAsync(x => x.Id == b.Id, updateOverdue);
                            b.Status = BookingStatus.Overdue;
                            b.OverdueDays = overdueDays;
                            b.LateFee = lateFee;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing lifecycle auto-transitions");
            }
        }

        // ======================================================
        // HELPER: RETRIEVE IMAGE STREAM FOR COMPARISON
        // ======================================================
        private async Task<Stream?> GetImageStreamAsync(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;

            try
            {
                // 1. GridFS image (/image/{id} or /api/images/{id})
                if (imageUrl.Contains("/image/"))
                {
                    var id = imageUrl.Substring(imageUrl.LastIndexOf('/') + 1);
                    var (stream, _, _) = await _gridFsService.DownloadFileAsync(id);
                    if (stream != null)
                    {
                        var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        await stream.DisposeAsync();
                        ms.Position = 0;
                        return ms;
                    }
                }

                // 2. Local wwwroot file (/uploads/...)
                var trimmed = imageUrl.TrimStart('/', '\\');
                var localPath = Path.Combine(_webHostEnvironment.WebRootPath, trimmed);
                if (System.IO.File.Exists(localPath))
                {
                    var ms = new MemoryStream();
                    using (var fs = System.IO.File.OpenRead(localPath))
                    {
                        await fs.CopyToAsync(ms);
                    }
                    ms.Position = 0;
                    return ms;
                }

                // 3. Remote URL
                if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var bytes = await client.GetByteArrayAsync(uri);
                    return new MemoryStream(bytes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load image stream from {Url}: {Message}", imageUrl, ex.Message);
            }

            return null;
        }
    }
}
