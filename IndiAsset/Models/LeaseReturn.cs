
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

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DamageDeduction { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal OtherDeduction { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal RefundAmount { get; set; }

        public string? InspectionNotes { get; set; }

        public bool IsInspected { get; set; } = false;
        public bool IsSettled { get; set; } = false;

        public DateTime? ReturnedAt { get; set; }
        public DateTime? SettledAt { get; set; }
    }
}