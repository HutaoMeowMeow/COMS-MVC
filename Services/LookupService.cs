using Microsoft.Extensions.Caching.Memory;

namespace COMS_MVC.Services
{
    public interface ILookupService
    {
        Task<List<string>> GetValuesAsync(string category);
        Task<List<LookupOption>> GetOptionsAsync(string category);
        Task<bool> IsValidAsync(string category, string? value);
        void Invalidate(string? category = null);
    }

    /// <summary>
    /// Shared DB-driven dropdown source. Every dropdown choice comes from the
    /// LookupOptions table in PostgreSQL — there is intentionally NO runtime
    /// fallback list: if the query fails, the error propagates like any other
    /// database failure instead of silently serving hardcoded values.
    /// Results are cached for 5 minutes; database edits become visible when
    /// the cache entry expires. <see cref="SeedData"/> is the one-time seed
    /// definition (written to the table by the seeder), not a fallback.
    /// </summary>
    public class LookupService : ILookupService
    {
        public static class Categories
        {
            public const string ReportType = "ReportType";
            public const string ReportStatus = "ReportStatus";
            public const string TicketCategory = "TicketCategory";
            public const string TicketPriority = "TicketPriority";
            public const string TicketStatus = "TicketStatus";
            public const string NotificationType = "NotificationType";
            public const string AnnouncementAudience = "AnnouncementAudience";
            public const string SensorType = "SensorType";
            public const string SensorStatus = "SensorStatus";
            public const string CanalStatus = "CanalStatus";
            public const string AlertStatus = "AlertStatus";
            public const string AlertSeverity = "AlertSeverity";
        }

        /// <summary>
        /// Initial choices seeded into LookupOptions (idempotent; existing
        /// rows are never overwritten except label/order repair). Order here
        /// defines the display order via SortOrder.
        /// </summary>
        public static readonly Dictionary<string, string[]> SeedData = new()
        {
            [Categories.ReportType] = new[] { "Garbage / Debris", "Blocked Canal", "Flooding", "Damaged Canal", "Unusual Water Level" },
            [Categories.ReportStatus] = new[] { "Pending", "Under Review", "Verified", "Assigned", "In Progress", "Resolved", "Rejected" },
            [Categories.TicketCategory] = new[] { "Account", "Report", "Technical Issue", "General Inquiry", "Other" },
            [Categories.TicketPriority] = new[] { "Low", "Normal", "High", "Urgent" },
            [Categories.TicketStatus] = new[] { "Open", "In Progress", "Waiting for User", "Resolved", "Closed" },
            [Categories.NotificationType] = new[] { "General", "Report Update", "Customer Service", "Account", "System", "Important", "Alert", "Report", "Announcement", "Maintenance", "Support" },
            [Categories.AnnouncementAudience] = new[] { "All", "Admin", "LGU", "Barangay", "Maintenance", "Resident", "LGU_Barangay", "Maintenance_Resident" },
            [Categories.SensorType] = new[] { "Water Level", "Flow Rate", "Debris", "Turbidity", "Temperature" },
            [Categories.SensorStatus] = new[] { "Online", "Offline", "Maintenance" },
            [Categories.CanalStatus] = new[] { "Normal", "Warning", "Critical", "Inactive" },
            [Categories.AlertStatus] = new[] { "Active", "Acknowledged", "In Progress", "Resolved", "Dismissed" },
            [Categories.AlertSeverity] = new[] { "Critical", "High", "Medium", "Low" },
        };

        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;

        public LookupService(ApplicationDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<List<LookupOption>> GetOptionsAsync(string category)
        {
            var key = $"lookup:{category}";
            if (_cache.TryGetValue(key, out List<LookupOption>? cached) && cached != null)
                return cached;
            // Database is the sole source. No fallback: an empty table yields
            // an empty list and a database failure throws to the caller.
            var options = await _db.LookupOptions
                .Where(o => o.Category == category && o.IsActive)
                .OrderBy(o => o.SortOrder).ThenBy(o => o.DisplayName)
                .ToListAsync();
            _cache.Set(key, options, TimeSpan.FromMinutes(5));
            return options;
        }

        public async Task<List<string>> GetValuesAsync(string category) =>
            (await GetOptionsAsync(category)).Select(o => o.Value).ToList();

        public async Task<bool> IsValidAsync(string category, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var values = await GetValuesAsync(category);
            return values.Contains(value.Trim());
        }

        public void Invalidate(string? category = null)
        {
            if (category == null)
            {
                foreach (var c in SeedData.Keys) _cache.Remove($"lookup:{c}");
            }
            else _cache.Remove($"lookup:{category}");
        }
    }
}
