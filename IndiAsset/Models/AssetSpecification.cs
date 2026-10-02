
using MongoDB.Bson.Serialization.Attributes;

namespace IndiAsset.Models
{
    [BsonIgnoreExtraElements]
    public class AssetSpecification
    {
        public string Name { get; set; } = string.Empty;

        // Alias for compatibility
        public string Key
        {
            get => Name;
            set => Name = value;
        }

        public string Value { get; set; } = string.Empty;
    }
}