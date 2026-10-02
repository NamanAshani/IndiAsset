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

        public ChatHub(MongoDbService mongoDbService, UserManager<ApplicationUser> userManager)
        {
            _mongoDbService = mongoDbService;
            _userManager = userManager;
        }

        public override async Task OnConnectedAsync()
        {
            var user = await _userManager.GetUserAsync(Context.User!);
            if (user != null && !string.IsNullOrEmpty(user.Id))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, user.Id);
            }
            await base.OnConnectedAsync();
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

        public async Task<object?> SendMessage(string conversationId, string receiverId, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var sender = await _userManager.GetUserAsync(Context.User!);
            if (sender == null || string.IsNullOrEmpty(sender.Id))
            {
                throw new HubException("User is not authenticated");
            }

            var message = new Message
            {
                ConversationId = conversationId,
                SenderId = sender.Id,
                ReceiverId = receiverId,
                Content = content.Trim(),
                SentAt = DateTime.UtcNow,
                IsRead = false
            };

            await _mongoDbService.Messages.InsertOneAsync(message);

            // Update conversation LastMessageAt
            var update = Builders<Conversation>.Update.Set(c => c.LastMessageAt, message.SentAt);
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

            // Broadcast to everyone in the conversation group
            await Clients.Group($"conv_{conversationId}").SendAsync("ReceiveMessage", messagePayload);

            // Also notify the receiver individually in case they are outside the active conversation view
            if (!string.IsNullOrEmpty(receiverId) && receiverId != sender.Id)
            {
                await Clients.Group(receiverId).SendAsync("NewMessageNotification", messagePayload);
            }

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
