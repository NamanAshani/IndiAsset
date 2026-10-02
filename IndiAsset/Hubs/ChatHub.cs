using IndiAsset.Models;
using IndiAsset.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;

namespace IndiAsset.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly MongoDbService _mongoDbService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly PresenceTracker _presenceTracker;

        public ChatHub(
            MongoDbService mongoDbService,
            UserManager<ApplicationUser> userManager,
            PresenceTracker presenceTracker)
        {
            _mongoDbService = mongoDbService;
            _userManager = userManager;
            _presenceTracker = presenceTracker;
        }

        public override async Task OnConnectedAsync()
        {
            var user = await _userManager.GetUserAsync(Context.User!);
            var userId = user?.Id ?? _userManager.GetUserId(Context.User!) ?? Context.UserIdentifier;

            if (!string.IsNullOrEmpty(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, userId);
                var isFirstConnection = await _presenceTracker.UserConnected(userId, Context.ConnectionId);
                if (isFirstConnection)
                {
                    await Clients.Others.SendAsync("UserStatusChanged", userId, true);
                }
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var user = await _userManager.GetUserAsync(Context.User!);
            var userId = user?.Id ?? _userManager.GetUserId(Context.User!) ?? Context.UserIdentifier;

            if (!string.IsNullOrEmpty(userId))
            {
                var isNowOffline = await _presenceTracker.UserDisconnected(userId, Context.ConnectionId);
                if (isNowOffline)
                {
                    var now = DateTime.UtcNow;
                    if (user != null)
                    {
                        user.LastSeenAt = now;
                        await _userManager.UpdateAsync(user);
                    }
                    await Clients.Others.SendAsync("UserStatusChanged", userId, false, now.ToString("o"));
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task<string[]> GetOnlineUsers()
        {
            return await _presenceTracker.GetOnlineUsers();
        }

        public async Task JoinConversation(string conversationId)
        {
            if (!string.IsNullOrEmpty(conversationId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"conv_{conversationId}");
            }
        }

        public async Task LeaveConversation(string conversationId)
        {
            if (!string.IsNullOrEmpty(conversationId))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conv_{conversationId}");
            }
        }

        public async Task<object?> SendMessage(string conversationId, string? receiverId, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var sender = await _userManager.GetUserAsync(Context.User!);
            if (sender == null || string.IsNullOrEmpty(sender.Id))
            {
                var fallbackId = _userManager.GetUserId(Context.User!) ?? Context.UserIdentifier;
                if (!string.IsNullOrEmpty(fallbackId))
                {
                    sender = await _userManager.FindByIdAsync(fallbackId);
                }
            }

            if (sender == null || string.IsNullOrEmpty(sender.Id))
            {
                throw new HubException("User is not authenticated");
            }

            // Always verify conversation from DB to ensure receiverId is 100% accurate
            var conv = await _mongoDbService.Conversations.Find(c => c.Id == conversationId).FirstOrDefaultAsync();

            string actualReceiverId = receiverId ?? string.Empty;
            if (conv != null)
            {
                if (string.IsNullOrEmpty(actualReceiverId) || actualReceiverId == sender.Id)
                {
                    actualReceiverId = conv.OwnerId == sender.Id ? conv.RenterId : conv.OwnerId;
                    if (string.IsNullOrEmpty(actualReceiverId) && conv.ParticipantIds != null)
                    {
                        actualReceiverId = conv.ParticipantIds.FirstOrDefault(id => id != sender.Id) ?? string.Empty;
                    }
                }
            }

            var message = new Message
            {
                ConversationId = conversationId,
                SenderId = sender.Id,
                ReceiverId = actualReceiverId,
                Content = content.Trim(),
                SentAt = DateTime.UtcNow,
                IsRead = false
            };

            await _mongoDbService.Messages.InsertOneAsync(message);

            // Update conversation snippet and LastMessageAt
            var update = Builders<Conversation>.Update
                .Set(c => c.LastMessageAt, message.SentAt)
                .Set(c => c.LastMessageContent, message.Content)
                .Set(c => c.LastMessageSenderId, sender.Id);

            await _mongoDbService.Conversations.UpdateOneAsync(c => c.Id == conversationId, update);

            var senderDisplayName = !string.IsNullOrWhiteSpace(sender.FullName) ? sender.FullName : (sender.UserName ?? "User");

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
                isRead = false
            };

            // 1. Send directly to caller (the active tab that submitted the message)
            await Clients.Caller.SendAsync("ReceiveMessage", messagePayload);

            // 2. Broadcast to conversation room group
            await Clients.Group($"conv_{conversationId}").SendAsync("ReceiveMessage", messagePayload);

            // 3. Send to actual receiver's group and user mapping across all their tabs
            if (!string.IsNullOrEmpty(actualReceiverId) && actualReceiverId != sender.Id)
            {
                await Clients.Group(actualReceiverId).SendAsync("ReceiveMessage", messagePayload);
                await Clients.User(actualReceiverId).SendAsync("ReceiveMessage", messagePayload);
            }

            // 4. Send to sender's user group (for multi-tab synchronization)
            await Clients.Group(sender.Id).SendAsync("ReceiveMessage", messagePayload);
            await Clients.User(sender.Id).SendAsync("ReceiveMessage", messagePayload);

            return messagePayload;
        }

        public async Task MarkAsRead(string messageId, string conversationId)
        {
            var update = Builders<Message>.Update
                .Set(m => m.IsRead, true)
                .Set(m => m.ReadAt, DateTime.UtcNow);

            await _mongoDbService.Messages.UpdateOneAsync(m => m.Id == messageId, update);

            if (!string.IsNullOrEmpty(conversationId))
            {
                await Clients.Group($"conv_{conversationId}").SendAsync("MessageRead", messageId);
            }
        }
    }
}
