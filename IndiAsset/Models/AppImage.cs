using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    [BsonIgnoreExtraElements]
    public class AppImage
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string ContentType { get; set; } = "image/jpeg";

        public byte[] Data { get; set; } = Array.Empty<byte>();

        public long Size { get; set; }

        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    }
}
