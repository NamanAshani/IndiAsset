using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    [BsonIgnoreExtraElements]
    public class Asset
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        // ApplicationUser.Id of the listing owner
        public string OwnerId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Examples: Machinery, Construction, Vehicles, Electronics, Agriculture, Event & Audio
        public string Category { get; set; } = string.Empty;

        // Rent per day
        [BsonRepresentation(BsonType.Decimal128)]
        public decimal DailyRent { get; set; }

        [BsonRepresentation(BsonType.Decimal128)]
        public decimal SecurityDeposit { get; set; }

        public string? City { get; set; }
        public string? State { get; set; }

        public List<AssetImage> Images { get; set; } = new();

        // Dynamic fields specific to this asset
        [BsonElement("Specifications")]
        public List<AssetSpecification> Specifications { get; set; } = new();

        // Dedicated maintenance & blackout calendar windows (replaces dummy self-leasing)
        public List<MaintenanceWindow> MaintenanceWindows { get; set; } = new();

        public bool IsAvailable { get; set; } = true;
        public bool IsApproved { get; set; } = true;
        public bool IsDeleted { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class MaintenanceWindow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string? Reason { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}