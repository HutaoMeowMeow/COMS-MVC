using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Notification
    {
        public int NotificationId { get; set; }

        [Display(Name = "User")]
        public int UserId { get; set; }

        [Display(Name = "Title")]
        [Required(ErrorMessage = "Title is required")]
        [StringLength(150, ErrorMessage = "Title cannot exceed 150 characters")]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "Message")]
        [Required(ErrorMessage = "Message is required")]
        [StringLength(2000, ErrorMessage = "Message cannot exceed 2000 characters")]
        public string Message { get; set; } = string.Empty;

        [Display(Name = "Type")]
        [Required(ErrorMessage = "Type is required")]
        [StringLength(50, ErrorMessage = "Type cannot exceed 50 characters")]
        public string Type { get; set; } = "General";

        [Display(Name = "Related Alert")]
        public int? RelatedAlertId { get; set; }

        [Display(Name = "Related Report")]
        public int? RelatedReportId { get; set; }

        [Display(Name = "Is Read")]
        public bool IsRead { get; set; } = false;

        [Display(Name = "Created At")]
        public DateTime CreatedAt { get; set; }

        public ApplicationUser? User { get; set; }

        public ObstructionAlert? RelatedAlert { get; set; }

        public CommunityReport? RelatedReport { get; set; }
    }
}
