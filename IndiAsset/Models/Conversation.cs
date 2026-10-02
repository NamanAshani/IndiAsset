using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    [BsonIgnoreExtraElements]
    public class Conversation
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonIgnoreIfNull]
        public string? BookingId { get; set; }

        public string OwnerId { get; set; } = string.Empty;
        public string RenterId { get; set; } = string.Empty;

        public List<string> ParticipantIds { get; set; } = new();

        public string? LastMessageContent { get; set; }
        public string? LastMessageSenderId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastMessageAt { get; set; }
    }
}