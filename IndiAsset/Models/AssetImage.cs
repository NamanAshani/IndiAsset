
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    [BsonIgnoreExtraElements]
    public class AssetImage
    {
        public string ImageUrl { get; set; } = string.Empty;

        // Alias for compatibility
        public string Url
        {
            get => ImageUrl;
            set => ImageUrl = value;
        }

        public bool IsPrimary { get; set; } = false;

        public int DisplayOrder { get; set; }

        public string? Caption { get; set; }
    }
}