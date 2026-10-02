using IndiAsset.Models;
using IndiAsset.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace IndiAsset.Controllers
{
    [Authorize]
    public class ChatController : Controller
    {
        private readonly MongoDbService _mongoDbService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly PresenceTracker _presenceTracker;

        public ChatController(
            MongoDbService mongoDbService,
            UserManager<ApplicationUser> userManager,
            PresenceTracker presenceTracker)
        {
            _mongoDbService = mongoDbService;
            _userManager = userManager;
            _presenceTracker = presenceTracker;
        }

        public async Task<IActionResult> Index(string? conversationId = null, string? recipientId = null)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null || string.IsNullOrEmpty(currentUser.Id))
            {
                return Challenge();
            }

            var currentUserId = currentUser.Id;

            // If recipientId is specified, start or locate conversation
            if (!string.IsNullOrEmpty(recipientId) && recipientId != currentUserId)
            {
                var existingConv = await _mongoDbService.Conversations
                    .Find(c => (c.OwnerId == currentUserId && c.RenterId == recipientId) ||
                               (c.OwnerId == recipientId && c.RenterId == currentUserId))
                    .FirstOrDefaultAsync();

                if (existingConv == null)
                {
                    existingConv = new Conversation
                    {
                        OwnerId = recipientId,
                        RenterId = currentUserId,
                        ParticipantIds = new List<string> { recipientId, currentUserId },
                        CreatedAt = DateTime.UtcNow,
                        LastMessageAt = DateTime.UtcNow
                    };
                    await _mongoDbService.Conversations.InsertOneAsync(existingConv);
                }

                conversationId = existingConv.Id;
            }

            // Fetch all conversations for current user
            var conversations = await _mongoDbService.Conversations
                .Find(c => c.OwnerId == currentUserId || c.RenterId == currentUserId || c.ParticipantIds.Contains(currentUserId))
                .SortByDescending(c => c.LastMessageAt)
                .ToListAsync();

            var conversationListItems = new List<ConversationListItemViewModel>();

            foreach (var conv in conversations)
            {
                var otherUserId = conv.OwnerId == currentUserId ? conv.RenterId : conv.OwnerId;
                if (string.IsNullOrEmpty(otherUserId) && conv.ParticipantIds != null)
                {
                    otherUserId = conv.ParticipantIds.FirstOrDefault(id => id != currentUserId) ?? string.Empty;
                }

                var otherUser = await _userManager.FindByIdAsync(otherUserId);

                string lastMessageContent = conv.LastMessageContent ?? string.Empty;
                DateTime? lastMessageAt = conv.LastMessageAt;

                if (string.IsNullOrEmpty(lastMessageContent))
                {
                    var lastMessage = await _mongoDbService.Messages
                        .Find(m => m.ConversationId == conv.Id)
                        .SortByDescending(m => m.SentAt)
                        .FirstOrDefaultAsync();

                    if (lastMessage != null)
                    {
                        lastMessageContent = lastMessage.Content;
                        lastMessageAt = lastMessage.SentAt;
                    }
                    else
                    {
                        lastMessageContent = "No messages yet";
                        lastMessageAt = conv.CreatedAt;
                    }
                }

                var unreadCount = await _mongoDbService.Messages
                    .CountDocumentsAsync(m => m.ConversationId == conv.Id && m.ReceiverId == currentUserId && !m.IsRead);

                var displayName = !string.IsNullOrWhiteSpace(otherUser?.FullName)
                    ? otherUser.FullName
                    : (otherUser?.UserName ?? "User");

                var isOnline = await _presenceTracker.IsUserOnline(otherUserId);

                conversationListItems.Add(new ConversationListItemViewModel
                {
                    ConversationId = conv.Id ?? string.Empty,
                    OtherUserId = otherUserId,
                    OtherUserName = displayName,
                    OtherUserEmail = otherUser?.Email ?? string.Empty,
                    LastMessageContent = lastMessageContent,
                    LastMessageAt = lastMessageAt,
                    UnreadCount = (int)unreadCount,
                    IsSelected = conv.Id == conversationId,
                    IsOnline = isOnline,
                    LastSeenAt = otherUser?.LastSeenAt
                });
            }

            // Default to first conversation if none specified
            if (string.IsNullOrEmpty(conversationId) && conversationListItems.Any())
            {
                conversationId = conversationListItems.First().ConversationId;
                conversationListItems.First().IsSelected = true;
            }

            var messages = new List<ChatMessageItemViewModel>();
            ApplicationUser? activeRecipient = null;
            bool activeRecipientIsOnline = false;

            if (!string.IsNullOrEmpty(conversationId))
            {
                var activeConv = conversations.FirstOrDefault(c => c.Id == conversationId);
                if (activeConv != null)
                {
                    var otherUserId = activeConv.OwnerId == currentUserId ? activeConv.RenterId : activeConv.OwnerId;
                    if (string.IsNullOrEmpty(otherUserId) && activeConv.ParticipantIds != null)
                    {
                        otherUserId = activeConv.ParticipantIds.FirstOrDefault(id => id != currentUserId) ?? string.Empty;
                    }

                    activeRecipient = await _userManager.FindByIdAsync(otherUserId);
                    if (activeRecipient != null)
                    {
                        activeRecipientIsOnline = await _presenceTracker.IsUserOnline(activeRecipient.Id);
                    }

                    var rawMessages = await _mongoDbService.Messages
                        .Find(m => m.ConversationId == conversationId)
                        .SortBy(m => m.SentAt)
                        .ToListAsync();

                    // Mark unread messages as read
                    var markReadUpdate = Builders<Message>.Update
                        .Set(m => m.IsRead, true)
                        .Set(m => m.ReadAt, DateTime.UtcNow);

                    await _mongoDbService.Messages.UpdateManyAsync(
                        m => m.ConversationId == conversationId && m.ReceiverId == currentUserId && !m.IsRead,
                        markReadUpdate);

                    foreach (var msg in rawMessages)
                    {
                        var isMine = msg.SenderId == currentUserId;
                        var senderName = isMine
                            ? (!string.IsNullOrWhiteSpace(currentUser.FullName) ? currentUser.FullName : currentUser.UserName ?? "You")
                            : (!string.IsNullOrWhiteSpace(activeRecipient?.FullName) ? activeRecipient.FullName : activeRecipient?.UserName ?? "User");

                        messages.Add(new ChatMessageItemViewModel
                        {
                            Id = msg.Id ?? string.Empty,
                            ConversationId = msg.ConversationId,
                            SenderId = msg.SenderId,
                            SenderName = senderName,
                            Content = msg.Content,
                            SentAt = msg.SentAt,
                            IsMine = isMine,
                            IsRead = msg.IsRead
                        });
                    }
                }
            }

            // Fetch available users for starting new conversations
            var allUsers = _userManager.Users
                .Where(u => u.Id != currentUserId)
                .Take(20)
                .ToList();

            var viewModel = new ChatViewModel
            {
                CurrentUserId = currentUserId,
                CurrentUserName = !string.IsNullOrWhiteSpace(currentUser.FullName) ? currentUser.FullName : currentUser.UserName ?? "You",
                ActiveConversationId = conversationId,
                ActiveRecipient = activeRecipient,
                ActiveRecipientIsOnline = activeRecipientIsOnline,
                ActiveRecipientLastSeenAt = activeRecipient?.LastSeenAt,
                Conversations = conversationListItems,
                Messages = messages,
                AvailableUsers = allUsers
            };

            return View(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> GetMessages(string conversationId, DateTime? after = null)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return Unauthorized();

            var filter = Builders<Message>.Filter.Eq(m => m.ConversationId, conversationId);
            if (after.HasValue)
            {
                filter &= Builders<Message>.Filter.Gt(m => m.SentAt, after.Value);
            }

            var rawMessages = await _mongoDbService.Messages
                .Find(filter)
                .SortBy(m => m.SentAt)
                .ToListAsync();

            var result = rawMessages.Select(m => new
            {
                id = m.Id,
                conversationId = m.ConversationId,
                senderId = m.SenderId,
                content = m.Content,
                sentAt = m.SentAt.ToString("o"),
                sentAtDisplay = m.SentAt.ToLocalTime().ToString("hh:mm tt"),
                isMine = m.SenderId == currentUser.Id,
                isRead = m.IsRead
            });

            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> StartChat(string recipientId)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null) return Unauthorized();

            if (recipientId == currentUser.Id)
            {
                return RedirectToAction("Index");
            }

            var existingConv = await _mongoDbService.Conversations
                .Find(c => (c.OwnerId == currentUser.Id && c.RenterId == recipientId) ||
                           (c.OwnerId == recipientId && c.RenterId == currentUser.Id))
                .FirstOrDefaultAsync();

            if (existingConv == null)
            {
                existingConv = new Conversation
                {
                    OwnerId = recipientId,
                    RenterId = currentUser.Id,
                    ParticipantIds = new List<string> { recipientId, currentUser.Id },
                    CreatedAt = DateTime.UtcNow,
                    LastMessageAt = DateTime.UtcNow
                };
                await _mongoDbService.Conversations.InsertOneAsync(existingConv);
            }

            return RedirectToAction("Index", new { conversationId = existingConv.Id });
        }
    }
}
