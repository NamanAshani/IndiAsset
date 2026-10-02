namespace IndiAsset.Models
{
    public class ChatViewModel
    {
        public string CurrentUserId { get; set; } = string.Empty;
        public string CurrentUserName { get; set; } = string.Empty;
        public string? ActiveConversationId { get; set; }
        public ApplicationUser? ActiveRecipient { get; set; }
        public List<ConversationListItemViewModel> Conversations { get; set; } = new();
        public List<ChatMessageItemViewModel> Messages { get; set; } = new();
        public List<ApplicationUser> AvailableUsers { get; set; } = new();
    }

    public class ConversationListItemViewModel
    {
        public string ConversationId { get; set; } = string.Empty;
        public string OtherUserId { get; set; } = string.Empty;
        public string OtherUserName { get; set; } = string.Empty;
        public string OtherUserEmail { get; set; } = string.Empty;
        public string LastMessageContent { get; set; } = string.Empty;
        public DateTime? LastMessageAt { get; set; }
        public int UnreadCount { get; set; }
        public bool IsSelected { get; set; }
    }

    public class ChatMessageItemViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public string SenderId { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public string FormattedTime => SentAt.ToLocalTime().ToString("hh:mm tt");
        public bool IsMine { get; set; }
        public bool IsRead { get; set; }
    }
}
