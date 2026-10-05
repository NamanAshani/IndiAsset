
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    public class LeaseReturn
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string BookingId { get; set; } = string.Empty;

        public string OwnerId { get; set; } = string.Empty;
        public string RenterId { get; set; } = string.Empty;

        public string ConditionBefore { get; set; } = string.Empty;
        public string ConditionAfter { get; set; } = string.Empty;

        public List<string> ReturnImageUrls { get; set; } = new();

        // Fresh Pre-Lease Dispatch Handover Baseline Images
        public List<string> DispatchImageUrls { get; set; } = new();

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DamageDeduction { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal OtherDeduction { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal RefundAmount { get; set; }

        public int QualityScore { get; set; } = 96;

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DiscrepancyPercentage { get; set; }

        public string ConditionCategory { get; set; } = "Excellent";

        public string? ComparisonSummary { get; set; }

        public string CleanlinessStatus { get; set; } = "Clean & Well-Maintained";

        public string FunctionalStatus { get; set; } = "Operational & Complete";

        public bool IsDamageDetected => QualityScore < 90 || DamageDeduction > 0;

        public string? InspectionNotes { get; set; }

        public bool IsInspected { get; set; } = false;
        public bool IsSettled { get; set; } = false;

        // Renter Dispute Workflow on Damage Deductions
        public bool IsDisputed { get; set; } = false;
        public string? DisputeReason { get; set; }
        public DateTime? DisputedAt { get; set; }
        public List<string> DisputeImageUrls { get; set; } = new();
        public string? DisputeStatus { get; set; } = "None"; // "None", "PendingReview", "Resolved", "AcceptedByRenter"
        public DateTime? DisputeResolvedAt { get; set; }
        public string? DisputeResolutionNotes { get; set; }

        public DateTime? ReturnedAt { get; set; }
        public DateTime? SettledAt { get; set; }
    }
}