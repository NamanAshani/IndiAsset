
namespace IndiAsset.Models
{
    public class AssetImage
    {
        public string ImageUrl { get; set; } = string.Empty;

        public bool IsPrimary { get; set; } = false;

        public int DisplayOrder { get; set; }
    }
}