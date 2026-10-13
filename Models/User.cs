using Microsoft.AspNetCore.Identity;

namespace COMS_MVC.Models
{
    public class ApplicationUser : IdentityUser<int>
    {
        public string FullName { get; set; }

        public string? Barangay { get; set; }

        public string? City { get; set; }

        public string? Address { get; set; }

        public DateTime CreatedAt { get; set; }

        public ICollection<CommunityReport> CommunityReports { get; set; } = new List<CommunityReport>();

        public ICollection<SupportTicket> SupportTickets { get; set; } = new List<SupportTicket>();

        public ICollection<SupportMessage> SupportMessages { get; set; } = new List<SupportMessage>();

        public ICollection<Notification> Notifications { get; set; } = new List<Notification>();

        public ICollection<Announcement> PostedAnnouncements { get; set; } = new List<Announcement>();
    }
}
