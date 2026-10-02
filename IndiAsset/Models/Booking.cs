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

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DailyRent { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal TotalRent { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal SecurityDeposit { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal TotalAmount { get; set; }

        public BookingStatus Status { get; set; } = BookingStatus.Pending;

        public string? Notes { get; set; }
        public string? AssetTitle { get; set; }
        public string? AssetImageUrl { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? ApprovedAt { get; set; }
        public DateTime? RejectedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
    }
}