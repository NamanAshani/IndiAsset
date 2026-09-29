
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json;

namespace IndiAsset.Models
{
    public class Asset
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        // SQL Server ApplicationUser.Id of the listing owner
        public string OwnerId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Examples: Electronics, Vehicles, Machinery
        public string Category { get; set; } = string.Empty;
        public string SubCategory { get; set; } = string.Empty;

        // Rent per day
        public decimal DailyRent { get; set; }

        public decimal SecurityDeposit { get; set; }

        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;

        public List<AssetImage> Images { get; set; } = new();

        // Dynamic fields specific to this asset
        [BsonElement("Specifications")]
        public List<AssetSpecification> Specifications { get; set; } = new();

        public bool IsAvailable { get; set; } = true;
        public bool IsApproved { get; set; } = false;
        public bool IsDeleted { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}