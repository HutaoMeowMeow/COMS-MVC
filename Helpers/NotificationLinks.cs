using System.Security.Claims;

namespace COMS_MVC.Helpers
{
    /// <summary>
    /// Server-side destination for a notification. The controller/action names
    /// are resolved against the real COMS routes by the notification view and
    /// by <see cref="Controllers.NotificationsController.Open"/>.
    /// </summary>
    public sealed record NotificationTarget(
        string Controller,
        string Action,
        int? Id,
        string LinkLabel);

    /// <summary>
    /// Maps a notification to the module or record it is about.
    /// No URL is stored in the database: the view and controller generate it
    /// from the structured EntityType/EntityId reference, with a fallback to
    /// the existing RelatedAlertId/RelatedReportId columns for older rows.
    /// </summary>
    public static class NotificationLinks
    {
        public const string Report = "Report";
        public const string Alert = "Alert";
        public const string SupportTicket = "SupportTicket";
        public const string Announcement = "Announcement";

        public static bool IsSupportStaff(ClaimsPrincipal user) =>
            user.IsInRole("Admin") || user.IsInRole("CustomerService");

        /// <summary>
        /// Reads the structured reference, then legacy columns, then the
        /// notification type. Returns a null type for general/system notices.
        /// </summary>
        public static (string? Type, int? Id) ResolveEntity(Notification notification)
        {
            if (!string.IsNullOrWhiteSpace(notification.EntityType))
            {
                return (notification.EntityType.Trim(), notification.EntityId);
            }

            if (notification.RelatedAlertId.HasValue)
            {
                return (Alert, notification.RelatedAlertId);
            }

            if (notification.RelatedReportId.HasValue)
            {
                return (Report, notification.RelatedReportId);
            }

            var type = (notification.Type ?? string.Empty).Trim();
            if (Contains(type, "report")) return (Report, null);
            if (Contains(type, "support") || Contains(type, "customer")) return (SupportTicket, null);
            if (Contains(type, "announcement")) return (Announcement, null);
            if (Contains(type, "alert") || Contains(type, "maintenance")) return (Alert, null);

            return (null, null);
        }

        /// <summary>
        /// Destination used to render a clickable notification. Returns null
        /// when the notification is informational and has no owning module.
        /// </summary>
        public static NotificationTarget? GetTarget(Notification notification, ClaimsPrincipal user)
        {
            var (type, id) = ResolveEntity(notification);
            return GetTarget(type, id, user, notification.Type);
        }

        public static NotificationTarget? GetTarget(
            string? entityType,
            int? entityId,
            ClaimsPrincipal user,
            string? notificationType = null)
        {
            switch (entityType)
            {
                case Report:
                    return entityId.HasValue
                        ? new NotificationTarget("Reports", "Details", entityId, "Open report")
                        : ReportsModule(user);

                case Alert:
                    return entityId.HasValue
                        ? new NotificationTarget("Alerts", "Details", entityId, "View alert")
                        : new NotificationTarget("Alerts", "Index", null, "View alerts");

                case SupportTicket:
                    return SupportModule(user, entityId);

                case Announcement:
                    return entityId.HasValue
                        ? new NotificationTarget("Announcements", "Details", entityId, "Read announcement")
                        : new NotificationTarget("Announcements", "Index", null, "View announcements");

                case "Account":
                    return new NotificationTarget("Account", "Profile", null, "Open profile");
            }

            // Older/custom notification types without a structured reference.
            var type = notificationType ?? string.Empty;
            if (Contains(type, "report")) return ReportsModule(user);
            if (Contains(type, "support") || Contains(type, "customer")) return SupportModule(user, null);
            if (Contains(type, "announcement")) return new NotificationTarget("Announcements", "Index", null, "View announcements");
            if (Contains(type, "alert") || Contains(type, "maintenance")) return new NotificationTarget("Alerts", "Index", null, "View alerts");
            if (Contains(type, "account")) return new NotificationTarget("Account", "Profile", null, "Open profile");

            return null;
        }

        /// <summary>Module page used when a linked record no longer exists.</summary>
        public static NotificationTarget Fallback(string? entityType, ClaimsPrincipal user) =>
            entityType switch
            {
                Report => ReportsModule(user),
                Alert => new NotificationTarget("Alerts", "Index", null, "View alerts"),
                SupportTicket => SupportModule(user, null),
                Announcement => new NotificationTarget("Announcements", "Index", null, "View announcements"),
                _ => new NotificationTarget("Notifications", "Index", null, "Back to notifications")
            };

        public static NotificationTarget ReportsModule(ClaimsPrincipal user) =>
            user.IsInRole("Resident")
                ? new NotificationTarget("Reports", "MyReports", null, "View my reports")
                : new NotificationTarget("Reports", "Index", null, "View community reports");

        public static NotificationTarget SupportModule(ClaimsPrincipal user, int? ticketId)
        {
            if (IsSupportStaff(user))
            {
                return ticketId.HasValue
                    ? new NotificationTarget("CustomerService", "Manage", ticketId, "Open conversation")
                    : new NotificationTarget("CustomerService", "Dashboard", null, "Open Customer Service");
            }

            if (user.IsInRole("Resident"))
            {
                return ticketId.HasValue
                    ? new NotificationTarget("CustomerService", "Ticket", ticketId, "Open conversation")
                    : new NotificationTarget("CustomerService", "Index", null, "Open Help Center");
            }

            return new NotificationTarget("Notifications", "Index", null, "Back to notifications");
        }

        /// <summary>
        /// Audience groups shared by announcement listing, details authorization,
        /// and notification destination validation. Keep this the single source.
        /// </summary>
        public static string AnnouncementAudienceGroup(string role) =>
            role switch
            {
                "LGU" => "LGU_Barangay",
                "Barangay" => "LGU_Barangay",
                "Maintenance" => "Maintenance_Resident",
                "Resident" => "Maintenance_Resident",
                _ => "All"
            };

        public static HashSet<string> AllowedAnnouncementAudiences(IEnumerable<string> roles)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "All" };
            foreach (var role in roles)
            {
                allowed.Add(role);
                allowed.Add(AnnouncementAudienceGroup(role));
            }
            return allowed;
        }

        private static bool Contains(string value, string token) =>
            value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
