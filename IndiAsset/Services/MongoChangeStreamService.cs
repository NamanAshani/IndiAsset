using IndiAsset.Hubs;
using IndiAsset.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;

namespace IndiAsset.Services
{
    /// <summary>
    /// Background service that listens to MongoDB Atlas Change Streams for the Messages collection.
    /// Whenever any device, external API, or server instance stores a message in MongoDB Atlas,
    /// this service immediately pushes it in real-time to all connected SignalR clients.
    /// </summary>
    public class MongoChangeStreamService : BackgroundService
    {
        private readonly MongoDbService _mongoDbService;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<MongoChangeStreamService> _logger;

        public MongoChangeStreamService(
            MongoDbService mongoDbService,
            IHubContext<ChatHub> hubContext,
            IServiceProvider serviceProvider,
            ILogger<MongoChangeStreamService> logger)
        {
            _mongoDbService = mongoDbService;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting MongoDB Atlas Change Stream watcher for Messages collection...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<Message>>()
                        .Match(change => change.OperationType == ChangeStreamOperationType.Insert);

                    using var cursor = await _mongoDbService.Messages.WatchAsync(
                        pipeline,
                        new ChangeStreamOptions { FullDocument = ChangeStreamFullDocumentOption.UpdateLookup },
                        stoppingToken);

                    while (await cursor.MoveNextAsync(stoppingToken))
                    {
                        foreach (var change in cursor.Current)
                        {
                            var message = change.FullDocument;
                            if (message == null) continue;

                            string senderDisplayName = "User";

                            using (var scope = _serviceProvider.CreateScope())
                            {
                                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                                var sender = await userManager.FindByIdAsync(message.SenderId);
                                if (sender != null)
                                {
                                    senderDisplayName = !string.IsNullOrWhiteSpace(sender.FullName)
                                        ? sender.FullName
                                        : (sender.UserName ?? "User");
                                }
                            }

                            var messagePayload = new
                            {
                                id = message.Id,
                                conversationId = message.ConversationId,
                                senderId = message.SenderId,
                                senderName = senderDisplayName,
                                receiverId = message.ReceiverId,
                                content = message.Content,
                                sentAt = message.SentAt.ToString("o"),
                                sentAtDisplay = message.SentAt.ToLocalTime().ToString("hh:mm tt"),
                                isRead = message.IsRead
                            };

                            // Broadcast to conversation group
                            await _hubContext.Clients.Group($"conv_{message.ConversationId}")
                                .SendAsync("ReceiveMessage", messagePayload, cancellationToken: stoppingToken);

                            // Broadcast to receiver's user group
                            if (!string.IsNullOrEmpty(message.ReceiverId))
                            {
                                await _hubContext.Clients.Group(message.ReceiverId)
                                    .SendAsync("ReceiveMessage", messagePayload, cancellationToken: stoppingToken);
                                await _hubContext.Clients.User(message.ReceiverId)
                                    .SendAsync("ReceiveMessage", messagePayload, cancellationToken: stoppingToken);
                            }

                            // Also broadcast to all connected clients to ensure any device listening picks it up
                            await _hubContext.Clients.All
                                .SendAsync("ReceiveMessage", messagePayload, cancellationToken: stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MongoDB Change Stream encountered an issue or connection retry: {Message}", ex.Message);
                    // Wait before reconnecting to Change Stream
                    await Task.Delay(3000, stoppingToken);
                }
            }
        }
    }
}
