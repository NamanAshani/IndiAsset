
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    public class Conversation
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string BookingId { get; set; } = string.Empty;

        public string OwnerId { get; set; } = string.Empty;
        public string RenterId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastMessageAt { get; set; }
    }
}