using AspNetCoreIdentity.MongoDriver.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    [BsonIgnoreExtraElements]
    public class ApplicationUser : MongoUser<string>
    {
        public string FullName { get; set; } = string.Empty;

        public string? Address { get; set; }

        public string? City { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;

        public string? ProfilePictureUrl { get; set; }

        public DateTime? LastSeenAt { get; set; }
    }
}
