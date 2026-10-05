using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace IndiAsset.Models
{
    public class ReturnAssetUploadViewModel
    {
        [Required]
        public string BookingId { get; set; } = string.Empty;

        public string AssetId { get; set; } = string.Empty;
        public string AssetTitle { get; set; } = string.Empty;
        public string AssetCategory { get; set; } = string.Empty;
        public string? PrimaryImageUrl { get; set; }
        public List<string> BaselineOriginalImageUrls { get; set; } = new();

        public string OwnerName { get; set; } = string.Empty;
        public string RenterName { get; set; } = string.Empty;

        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalDays { get; set; }
        public decimal DailyRent { get; set; }
        public decimal TotalRent { get; set; }
        public decimal SecurityDeposit { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal AmountToPay { get; set; }

        // Escrow adjustment fields
        public decimal DepositAdjusted { get; set; }
        public decimal ExcessDepositRefund { get; set; }
        public bool IsDepositAlreadyPaid { get; set; }
        public bool IsRentPaid { get; set; }

        // Razorpay payment fields
        public string? RazorpayKeyId { get; set; }
        public string? RazorpayOrderId { get; set; }
        public string? RazorpayPaymentId { get; set; }
        public string? RazorpaySignature { get; set; }
        public bool PaymentCompleted { get; set; }
        public bool IsSimulationMode { get; set; }

        // Photos uploaded after asset usage
        [Required(ErrorMessage = "Please upload at least one post-use photo of the asset for comparison")]
        public List<IFormFile> ReturnPhotos { get; set; } = new();

        // Renter notes on condition
        [StringLength(1000)]
        [Display(Name = "Condition Notes")]
        public string? ConditionNotes { get; set; }

        // Experience rating & review
        [Range(1, 5, ErrorMessage = "Please provide a rating between 1 and 5 stars")]
        public int Rating { get; set; } = 5;

        [Required(ErrorMessage = "Please write a brief review of the asset performance")]
        [StringLength(1000, MinimumLength = 5)]
        [Display(Name = "Asset Review")]
        public string ReviewComment { get; set; } = string.Empty;
    }

    public class InspectionReviewViewModel
    {
        public Booking Booking { get; set; } = new();
        public Asset Asset { get; set; } = new();
        public LeaseReturn LeaseReturn { get; set; } = new();
        public Review? Review { get; set; }
        public Payment? Payment { get; set; }

        public string OwnerName { get; set; } = string.Empty;
        public string RenterName { get; set; } = string.Empty;
        public bool IsOwner { get; set; }

        public List<string> BaselineImages { get; set; } = new();
        public List<string> ReturnImages { get; set; } = new();

        public int ConditionScore { get; set; } = 96;
        public decimal DiscrepancyPercentage { get; set; }
        public string ConditionSummary { get; set; } = "Excellent — No Structural Damage Detected";
        public string CleanlinessStatus { get; set; } = "Clean & Well Maintained";
        public string FunctionalStatus { get; set; } = "Operational & Complete";
        public bool FullDepositRefundRecommended { get; set; } = true;
        public decimal SecurityDeposit { get; set; }
        public decimal DamageDeduction { get; set; }
        public decimal RecommendedRefundAmount { get; set; }
        public bool IsDamageDetected => ConditionScore < 90 || DamageDeduction > 0;

        // Dispatch Baseline info
        public bool IsBaselineFromDispatch { get; set; }
        public DateTime? DispatchedAt { get; set; }
        public string? DispatchNotes { get; set; }

        // Dispute workflow properties
        public bool IsDisputed { get; set; }
        public string? DisputeReason { get; set; }
        public DateTime? DisputedAt { get; set; }
        public List<string> DisputeImages { get; set; } = new();
        public string? DisputeStatus { get; set; }
        public string? DisputeResolutionNotes { get; set; }
    }

    public class DispatchCheckInViewModel
    {
        [Required]
        public string BookingId { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public string AssetTitle { get; set; } = string.Empty;
        public string AssetCategory { get; set; } = string.Empty;
        public string? PrimaryImageUrl { get; set; }

        public string RenterName { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalDays { get; set; }
        public decimal TotalRent { get; set; }
        public decimal SecurityDeposit { get; set; }

        [Required(ErrorMessage = "Please upload at least 1 dispatch handover photo of the equipment")]
        public List<IFormFile> DispatchPhotos { get; set; } = new();

        [StringLength(1000)]
        [Display(Name = "Dispatch Handover Remarks")]
        public string? DispatchNotes { get; set; }
    }

    public class PayDepositViewModel
    {
        public string BookingId { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public string AssetTitle { get; set; } = string.Empty;
        public string AssetCategory { get; set; } = string.Empty;
        public string? PrimaryImageUrl { get; set; }

        public string OwnerId { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public string RenterId { get; set; } = string.Empty;
        public string RenterName { get; set; } = string.Empty;

        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalDays { get; set; }
        public decimal DailyRent { get; set; }
        public decimal TotalRent { get; set; }
        public bool IsNegotiated { get; set; }
        public decimal OriginalDailyRent { get; set; }
        public decimal OriginalTotalRent { get; set; }
        public decimal SecurityDeposit { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal TotalUpfrontAmount => (IsRentPaid ? 0 : TotalRent) + (IsSecurityDepositPaid ? 0 : SecurityDeposit);
        public bool IsRentPaid { get; set; }
        public bool IsSecurityDepositPaid { get; set; }

        // Negotiation fields for PayDeposit view
        public decimal? NegotiatedDailyRent { get; set; }
        public decimal? ProposedNegotiatedDailyRent { get; set; }
        public decimal? ProposedNegotiatedTotalRent => ProposedNegotiatedDailyRent.HasValue ? ProposedNegotiatedDailyRent.Value * TotalDays : null;
        public string? NegotiationOfferedByUserId { get; set; }
        public string? NegotiationStatus { get; set; }
        public string? NegotiationNotes { get; set; }
        public DateTime? NegotiatedAt { get; set; }
        public BookingStatus Status { get; set; }
        public bool CanNegotiate { get; set; } = true;
        public bool IsPriceAgreed { get; set; }
        public DateTime? PriceAgreedAt { get; set; }
        public decimal? AgreedDailyRent { get; set; }

        // Razorpay payment fields for deposit
        public string? RazorpayKeyId { get; set; }
        public string? RazorpayOrderId { get; set; }
        public string? RazorpayPaymentId { get; set; }
        public string? RazorpaySignature { get; set; }
        public bool IsSimulationMode { get; set; }
    }
}
