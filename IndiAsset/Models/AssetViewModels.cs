using System.ComponentModel.DataAnnotations;

namespace IndiAsset.Models
{
    public class AssetCardViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public decimal DailyRent { get; set; }
        public decimal SecurityDeposit { get; set; }
        public string? City { get; set; }
        public string? PrimaryImageUrl { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public bool IsCurrentlyOccupied { get; set; }
        public string OccupancyStatusText => IsCurrentlyOccupied ? "Currently Occupied" : "Available Now";
        public DateTime? OccupiedUntil { get; set; }
        public DateTime AvailableFrom { get; set; }
        public bool IsOwner { get; set; }
    }

    public class AssetSearchViewModel
    {
        public string? SearchQuery { get; set; }
        public string? SelectedCategory { get; set; }
        public string? City { get; set; }
        public decimal? MinPrice { get; set; }
        public decimal? MaxPrice { get; set; }
        public string? AvailabilityFilter { get; set; } = "all"; // all, available, occupied
        public string? SortBy { get; set; } = "newest"; // newest, price_asc, price_desc, title

        public List<AssetCardViewModel> Assets { get; set; } = new();
        public List<string> Categories { get; set; } = new();
        public List<string> Cities { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class AssetDetailsViewModel
    {
        public Asset Asset { get; set; } = new();
        public ApplicationUser? Owner { get; set; }
        public bool IsCurrentlyOccupied { get; set; }
        public DateTime? OccupiedUntil { get; set; }
        public DateTime AvailableFrom { get; set; }
        public int ActiveBookingsCount { get; set; }
        public List<Booking> ConfirmedBookings { get; set; } = new();
        public List<BookingItemViewModel> OwnerPendingBookings { get; set; } = new();
        public List<BookingItemViewModel> OwnerActiveBookings { get; set; } = new();
        public List<BookingItemViewModel> OwnerRecentHistory { get; set; } = new();
        public int TotalBookingsCount { get; set; }
        public decimal TotalRevenueEarned { get; set; }
        public bool IsOwner { get; set; }
        public bool CanBook { get; set; }
        public BookingCreateViewModel BookingForm { get; set; } = new();
    }

    public class AssetCreateEditViewModel
    {
        public string? Id { get; set; }

        [Required(ErrorMessage = "Title is required")]
        [StringLength(100, MinimumLength = 3)]
        public string Title { get; set; } = string.Empty;

        [Required(ErrorMessage = "Description is required")]
        public string Description { get; set; } = string.Empty;

        [Required(ErrorMessage = "Category is required")]
        public string Category { get; set; } = string.Empty;

        // Custom Category if "Other" is chosen
        public string? CustomCategory { get; set; }

        [Required(ErrorMessage = "Daily rent is required")]
        [Range(1, 1000000, ErrorMessage = "Daily rent must be greater than 0")]
        public decimal DailyRent { get; set; }

        [Required(ErrorMessage = "Security deposit is required")]
        [Range(0, 5000000, ErrorMessage = "Security deposit cannot be negative")]
        public decimal SecurityDeposit { get; set; }

        public string? City { get; set; }
        public string? State { get; set; }

        // Uploaded image files directly from device
        public List<IFormFile>? UploadedImages { get; set; }

        // Multiple image URLs separated by commas or newlines (optional fallback)
        public string? ImageUrls { get; set; }

        // Existing image URLs (for Edit view)
        public List<string> ExistingImageUrls { get; set; } = new();

        // Dynamic specifications in form of key/value pairs
        public List<AssetSpecification> Specifications { get; set; } = new();

        public bool IsAvailable { get; set; } = true;
    }

    public class BookingCreateViewModel
    {
        [Required]
        public string AssetId { get; set; } = string.Empty;

        [Required(ErrorMessage = "Start date is required")]
        [DataType(DataType.Date)]
        public DateTime StartDate { get; set; } = DateTime.UtcNow.Date.AddDays(1);

        [Required(ErrorMessage = "Number of days is required")]
        [Range(1, 365, ErrorMessage = "Lease duration must be between 1 and 365 days")]
        public int NumberOfDays { get; set; } = 3;

        [Required(ErrorMessage = "End date is required")]
        [DataType(DataType.Date)]
        public DateTime EndDate { get; set; } = DateTime.UtcNow.Date.AddDays(4);

        public string? Notes { get; set; }

        // Negotiation fields when requesting a lease
        public bool ProposeNegotiatedPrice { get; set; }

        [Range(1, 100000000, ErrorMessage = "Proposed daily rent must be greater than 0")]
        public decimal? ProposedDailyRent { get; set; }

        public string? NegotiationNotes { get; set; }
    }

    public class MyAssetsDashboardViewModel
    {
        public List<AssetCardViewModel> Assets { get; set; } = new();
        public List<BookingItemViewModel> PendingInquiries { get; set; } = new();
        public List<BookingItemViewModel> ActiveLeases { get; set; } = new();
        public int TotalAssetsCount => Assets.Count;
        public int OccupiedCount => Assets.Count(a => a.IsCurrentlyOccupied);
        public int AvailableCount => Assets.Count(a => !a.IsCurrentlyOccupied);
        public int PendingInquiriesCount => PendingInquiries.Count;
    }

    public class MyBookingsViewModel
    {
        public List<BookingItemViewModel> AsRenterBookings { get; set; } = new();
        public List<BookingItemViewModel> AsOwnerBookings { get; set; } = new();
        public string ActiveTab { get; set; } = "renter";
    }

    public class BookingItemViewModel
    {
        public string BookingId { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public string AssetTitle { get; set; } = string.Empty;
        public string? AssetImageUrl { get; set; }
        public string Category { get; set; } = string.Empty;
        public string OtherUserId { get; set; } = string.Empty;
        public string OtherUserName { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalDays => Math.Max(1, (int)(EndDate.Date - StartDate.Date).TotalDays);
        public decimal DailyRent { get; set; }
        public decimal TotalRent { get; set; }
        public decimal OriginalDailyRent { get; set; }
        public decimal OriginalTotalRent { get; set; }
        public bool IsNegotiated { get; set; }
        public decimal? NegotiatedDailyRent { get; set; }
        public decimal? ProposedNegotiatedDailyRent { get; set; }
        public decimal? ProposedNegotiatedTotalRent => ProposedNegotiatedDailyRent.HasValue ? ProposedNegotiatedDailyRent.Value * TotalDays : null;
        public string? NegotiationOfferedByUserId { get; set; }
        public string? NegotiationStatus { get; set; }
        public string? NegotiationNotes { get; set; }
        public DateTime? NegotiatedAt { get; set; }
        public bool CanNegotiate => !IsSecurityDepositPaid && (Status == BookingStatus.Approved || Status == BookingStatus.Pending);
        public bool IsPriceAgreed { get; set; }
        public DateTime? PriceAgreedAt { get; set; }
        public decimal? AgreedDailyRent { get; set; }
        public string? PriceAgreedByUserId { get; set; }
        public decimal SecurityDeposit { get; set; }
        public decimal TotalAmount { get; set; }
        public BookingStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsOwner { get; set; }
        public bool HasReturnInspection { get; set; }
        public string? ReturnInspectionId { get; set; }
        public bool IsSecurityDepositPaid { get; set; }
        public decimal SecurityDepositPaidAmount { get; set; }
        public DateTime? SecurityDepositPaidAt { get; set; }
        public bool IsRentPaid { get; set; }
        public decimal RentPaidAmount { get; set; }
        public DateTime? RentPaidAt { get; set; }
        public decimal UpfrontTotalAmount => TotalRent + SecurityDeposit;
    }
}
