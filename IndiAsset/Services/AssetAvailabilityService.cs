using IndiAsset.Models;
using MongoDB.Driver;

namespace IndiAsset.Services
{
    public class AssetAvailabilityInfo
    {
        public string AssetId { get; set; } = string.Empty;
        public bool IsCurrentlyOccupied { get; set; }
        public string OccupancyStatusText => IsCurrentlyOccupied ? "Currently Occupied" : "Available Now";
        public DateTime? OccupiedUntil { get; set; }
        public DateTime AvailableFrom { get; set; } = DateTime.UtcNow.Date;
        public int ActiveBookingsCount { get; set; }
        public List<Booking> ConfirmedBookings { get; set; } = new();
    }

    public class AssetAvailabilityService
    {
        private readonly MongoDbService _mongoDbService;

        public AssetAvailabilityService(MongoDbService mongoDbService)
        {
            _mongoDbService = mongoDbService;
        }

        /// <summary>
        /// Evaluates whether an asset is currently on lease/occupied, when it will be free, and future bookings.
        /// </summary>
        public async Task<AssetAvailabilityInfo> GetAvailabilityAsync(Asset asset)
        {
            var info = new AssetAvailabilityInfo
            {
                AssetId = asset.Id ?? string.Empty
            };

            // If the owner paused availability manually
            if (!asset.IsAvailable)
            {
                info.IsCurrentlyOccupied = true;
                return info;
            }

            var now = DateTime.UtcNow;

            var bookings = await _mongoDbService.Bookings
                .Find(b => b.AssetId == asset.Id &&
                          (b.Status == BookingStatus.Active || b.Status == BookingStatus.Approved) &&
                          b.EndDate >= now.Date)
                .SortBy(b => b.StartDate)
                .ToListAsync();

            info.ConfirmedBookings = bookings;
            info.ActiveBookingsCount = bookings.Count;

            var activeNow = bookings.FirstOrDefault(b => b.StartDate <= now && b.EndDate >= now);
            if (activeNow != null)
            {
                info.IsCurrentlyOccupied = true;
                info.OccupiedUntil = activeNow.EndDate;
                info.AvailableFrom = activeNow.EndDate.Date.AddDays(1);
            }
            else
            {
                info.IsCurrentlyOccupied = false;
                info.AvailableFrom = now.Date;
            }

            return info;
        }

        /// <summary>
        /// Batch evaluates availability and occupancy for multiple assets in a single query (prevents N+1 database queries).
        /// </summary>
        public async Task<Dictionary<string, AssetAvailabilityInfo>> GetAvailabilityBatchAsync(IEnumerable<string> assetIds)
        {
            var dict = new Dictionary<string, AssetAvailabilityInfo>();
            var idList = assetIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

            if (!idList.Any()) return dict;

            var now = DateTime.UtcNow;

            var bookings = await _mongoDbService.Bookings
                .Find(b => idList.Contains(b.AssetId) &&
                          (b.Status == BookingStatus.Active || b.Status == BookingStatus.Approved) &&
                          b.EndDate >= now.Date)
                .SortBy(b => b.StartDate)
                .ToListAsync();

            var grouped = bookings.GroupBy(b => b.AssetId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var assetId in idList)
            {
                var info = new AssetAvailabilityInfo { AssetId = assetId };
                if (grouped.TryGetValue(assetId, out var assetBookings))
                {
                    info.ConfirmedBookings = assetBookings;
                    info.ActiveBookingsCount = assetBookings.Count;

                    var activeNow = assetBookings.FirstOrDefault(b => b.StartDate <= now && b.EndDate >= now);
                    if (activeNow != null)
                    {
                        info.IsCurrentlyOccupied = true;
                        info.OccupiedUntil = activeNow.EndDate;
                        info.AvailableFrom = activeNow.EndDate.Date.AddDays(1);
                    }
                    else
                    {
                        info.IsCurrentlyOccupied = false;
                        info.AvailableFrom = now.Date;
                    }
                }
                else
                {
                    info.IsCurrentlyOccupied = false;
                    info.AvailableFrom = now.Date;
                }

                dict[assetId] = info;
            }

            return dict;
        }

        /// <summary>
        /// Checks if requested lease dates conflict with an existing active, approved, or deposit-secured booking.
        /// Optionally excludes a specific booking ID (e.g. when validating if the current booking itself can be approved).
        /// </summary>
        public async Task<bool> IsDateRangeAvailableAsync(string assetId, DateTime start, DateTime end, string? excludeBookingId = null)
        {
            // 1. Check if asset is undergoing scheduled maintenance or calendar blackout
            var asset = await _mongoDbService.Assets.Find(a => a.Id == assetId).FirstOrDefaultAsync();
            if (asset != null && asset.MaintenanceWindows != null)
            {
                var isUnderMaintenance = asset.MaintenanceWindows.Any(m => m.StartDate.Date <= end.Date && m.EndDate.Date >= start.Date);
                if (isUnderMaintenance) return false;
            }

            var filter = Builders<Booking>.Filter.Eq(b => b.AssetId, assetId) &
                         (Builders<Booking>.Filter.Eq(b => b.Status, BookingStatus.Active) |
                          Builders<Booking>.Filter.Eq(b => b.Status, BookingStatus.Approved) |
                          Builders<Booking>.Filter.Eq(b => b.Status, BookingStatus.Overdue) |
                          (Builders<Booking>.Filter.Eq(b => b.Status, BookingStatus.Pending) & Builders<Booking>.Filter.Eq(b => b.IsSecurityDepositPaid, true))) &
                         Builders<Booking>.Filter.Lte(b => b.StartDate, end) &
                         Builders<Booking>.Filter.Gte(b => b.EndDate, start);

            if (!string.IsNullOrEmpty(excludeBookingId))
            {
                filter &= Builders<Booking>.Filter.Ne(b => b.Id, excludeBookingId);
            }

            var conflicts = await _mongoDbService.Bookings.CountDocumentsAsync(filter);
            return conflicts == 0;
        }

        /// <summary>
        /// Retrieves the list of overlapping confirmed/active bookings for an asset within the specified date range.
        /// </summary>
        public async Task<List<Booking>> GetOverlappingBookingsAsync(string assetId, DateTime start, DateTime end, string? excludeBookingId = null)
        {
            var filter = Builders<Booking>.Filter.Eq(b => b.AssetId, assetId) &
                         (Builders<Booking>.Filter.Eq(b => b.Status, BookingStatus.Active) |
                          Builders<Booking>.Filter.Eq(b => b.Status, BookingStatus.Approved) |
                          (Builders<Booking>.Filter.Eq(b => b.Status, BookingStatus.Pending) & Builders<Booking>.Filter.Eq(b => b.IsSecurityDepositPaid, true))) &
                         Builders<Booking>.Filter.Lte(b => b.StartDate, end) &
                         Builders<Booking>.Filter.Gte(b => b.EndDate, start);

            if (!string.IsNullOrEmpty(excludeBookingId))
            {
                filter &= Builders<Booking>.Filter.Ne(b => b.Id, excludeBookingId);
            }

            return await _mongoDbService.Bookings.Find(filter).ToListAsync();
        }
    }
}
