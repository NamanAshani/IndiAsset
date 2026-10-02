using System.Collections.Concurrent;

namespace IndiAsset.Services
{
    public class PresenceTracker
    {
        // Tracks userId -> set of active connectionIds (for multiple tabs/devices)
        private static readonly ConcurrentDictionary<string, HashSet<string>> OnlineUsers = new();

        public Task<bool> UserConnected(string userId, string connectionId)
        {
            lock (OnlineUsers)
            {
                var connections = OnlineUsers.GetOrAdd(userId, _ => new HashSet<string>());
                connections.Add(connectionId);
                // Return true if this is the user's first connection (transitioned to online)
                return Task.FromResult(connections.Count == 1);
            }
        }

        public Task<bool> UserDisconnected(string userId, string connectionId)
        {
            lock (OnlineUsers)
            {
                if (!OnlineUsers.TryGetValue(userId, out var connections))
                {
                    return Task.FromResult(false);
                }

                connections.Remove(connectionId);
                if (connections.Count == 0)
                {
                    OnlineUsers.TryRemove(userId, out _);
                    // Return true if all connections are closed (transitioned to offline)
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        }

        public Task<string[]> GetOnlineUsers()
        {
            lock (OnlineUsers)
            {
                return Task.FromResult(OnlineUsers.Keys.ToArray());
            }
        }

        public Task<bool> IsUserOnline(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return Task.FromResult(false);
            lock (OnlineUsers)
            {
                return Task.FromResult(OnlineUsers.ContainsKey(userId));
            }
        }
    }
}
