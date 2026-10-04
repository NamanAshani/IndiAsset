using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    [BsonIgnoreExtraElements]
    public class Booking
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string AssetId { get; set; } = string.Empty;

        public string RenterId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;

        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalDays { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DailyRent { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal TotalRent { get; set; }

        // Original prices before any negotiation
        [BsonRepresentation(BsonType.Decimal128)]
        public decimal OriginalDailyRent { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal OriginalTotalRent { get; set; }

        // Negotiation tracking
        public bool IsNegotiated { get; set; } = false;

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal? NegotiatedDailyRent { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal? ProposedNegotiatedDailyRent { get; set; }

        public string? NegotiationOfferedByUserId { get; set; }
        public string? NegotiationStatus { get; set; } // null, "Offered", "Accepted", "Declined"
        public string? NegotiationNotes { get; set; }
        public DateTime? NegotiatedAt { get; set; }

        // Price agreement tracking (price must be agreed upon before deposit payment or approval)
        public bool IsPriceAgreed { get; set; } = false;
        public DateTime? PriceAgreedAt { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal? AgreedDailyRent { get; set; }
        public string? PriceAgreedByUserId { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal SecurityDeposit { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal TotalAmount { get; set; }

        public BookingStatus Status { get; set; } = BookingStatus.Pending;

        public string? Notes { get; set; }
        public string? AssetTitle { get; set; }
        public string? AssetImageUrl { get; set; }

        // Upfront Security Deposit Escrow Payment tracking
        public bool IsSecurityDepositPaid { get; set; } = false;
        public string? SecurityDepositPaymentId { get; set; }
        public string? SecurityDepositOrderId { get; set; }
        public DateTime? SecurityDepositPaidAt { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal SecurityDepositPaidAmount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? ApprovedAt { get; set; }
        public DateTime? RejectedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
    }
}