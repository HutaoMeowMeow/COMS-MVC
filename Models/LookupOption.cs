using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    /// <summary>
    /// Single table for all admin-manageable dropdown options.
    /// Category examples: ReportType, ReportStatus, TicketCategory,
    /// TicketPriority, TicketStatus, NotificationType, AnnouncementAudience,
    /// SensorType, SensorStatus, CanalStatus.
    /// Value is the stored key (e.g. "In Progress"); DisplayName is the label.
    /// </summary>
    public class LookupOption
    {
        public int LookupOptionId { get; set; }

        [Required]
        [StringLength(50)]
        public string Category { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Value { get; set; } = string.Empty;

        [Required]
        [StringLength(150)]
        public string DisplayName { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
