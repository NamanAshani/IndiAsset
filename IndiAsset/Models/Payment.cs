
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    public class Payment
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string BookingId { get; set; } = string.Empty;

        public string PayerId { get; set; } = string.Empty;
        public string PayeeId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal RentAmount { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DepositAmount { get; set; }

        public decimal TotalAmount =>
            RentAmount + DepositAmount;

        public PaymentStatus Status { get; set; }
            = PaymentStatus.Pending;

        public string PaymentMethod { get; set; } = string.Empty;

        // External payment gateway transaction reference
        public string? TransactionId { get; set; }
        public string? RazorpayOrderId { get; set; }
        public string? RazorpaySignature { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? PaidAt { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DepositRefunded { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DepositDeducted { get; set; }
    }
}