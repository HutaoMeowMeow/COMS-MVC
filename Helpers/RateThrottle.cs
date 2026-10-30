using Microsoft.Extensions.Caching.Memory;

namespace COMS_MVC.Helpers
{
    /// <summary>
    /// Small sliding-window throttle used for resident-initiated writes
    /// (chat messages, ticket creation). Keyed per user in IMemoryCache:
    /// bounded memory (entries expire), per-instance by design — a single
    /// Railway instance is the expected deployment scale.
    /// </summary>
    public static class RateThrottle
    {
        /// <summary>
        /// Returns false (with a user-facing error) when the user is inside
        /// the cooldown window or has exceeded maxActions within the window.
        /// Counters for rejected actions are NOT incremented.
        /// </summary>
        public static bool Check(
            IMemoryCache cache,
            string keyPrefix,
            TimeSpan cooldown,
            int maxActionsPerWindow,
            TimeSpan countWindow,
            string cooldownMessage,
            string limitMessage,
            out string? error)
        {
            error = null;
            var now = DateTime.UtcNow;
            var lastKey = $"{keyPrefix}:last";
            var countKey = $"{keyPrefix}:count";

            if (cache.TryGetValue<DateTime>(lastKey, out var last)
                && now - last < cooldown)
            {
                error = cooldownMessage;
                return false;
            }

            var count = cache.GetOrCreate(countKey, e =>
            {
                e.AbsoluteExpirationRelativeToNow = countWindow;
                return 0;
            });
            if (count >= maxActionsPerWindow)
            {
                error = limitMessage;
                return false;
            }

            cache.Set(countKey, count + 1, countWindow);
            if (cooldown > TimeSpan.Zero)
            {
                cache.Set(lastKey, now, cooldown);
            }
            return true;
        }
    }
}
