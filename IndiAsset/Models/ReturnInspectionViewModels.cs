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

        public string OwnerName { get; set; } = string.Empty;
        public string RenterName { get; set; } = string.Empty;
        public bool IsOwner { get; set; }

        public List<string> BaselineImages { get; set; } = new();
        public List<string> ReturnImages { get; set; } = new();

        public int ConditionScore { get; set; } = 96;
        public string ConditionSummary { get; set; } = "Excellent — No Structural Damage Detected";
        public string CleanlinessStatus { get; set; } = "Clean & Well Maintained";
        public string FunctionalStatus { get; set; } = "Operational & Complete";
        public bool FullDepositRefundRecommended { get; set; } = true;
        public decimal RecommendedRefundAmount { get; set; }
    }
}
