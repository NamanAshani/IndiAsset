
using MongoDB.Driver;
using IndiAsset.Models;

namespace IndiAsset.Services
{
    public class MongoDbService
    {
        private readonly IMongoDatabase _database;

        public MongoDbService(IConfiguration configuration)
        {
            var connectionString =
                configuration["MongoDB:ConnectionString"];

            var databaseName =
                configuration["MongoDB:DatabaseName"];

            if (string.IsNullOrWhiteSpace(connectionString) ||
                string.IsNullOrWhiteSpace(databaseName))
            {
                throw new InvalidOperationException(
                    "MongoDB configuration is missing.");
            }

            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.SslSettings = new SslSettings
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            };
            var client = new MongoClient(settings);

            _database = client.GetDatabase(databaseName);
        }

        public IMongoCollection<Asset> Assets =>
            _database.GetCollection<Asset>("Assets");

        public IMongoCollection<Booking> Bookings =>
            _database.GetCollection<Booking>("Bookings");

        public IMongoCollection<Payment> Payments =>
            _database.GetCollection<Payment>("Payments");

        public IMongoCollection<LeaseReturn> LeaseReturns =>
            _database.GetCollection<LeaseReturn>("LeaseReturns");

        public IMongoCollection<Conversation> Conversations =>
            _database.GetCollection<Conversation>("Conversations");

        public IMongoCollection<Message> Messages =>
            _database.GetCollection<Message>("Messages");

        public IMongoCollection<Notification> Notifications =>
            _database.GetCollection<Notification>("Notifications");

        public IMongoCollection<Review> Reviews =>
            _database.GetCollection<Review>("Reviews");

        public IMongoCollection<AppImage> AppImages =>
            _database.GetCollection<AppImage>("AppImages");
    }
}