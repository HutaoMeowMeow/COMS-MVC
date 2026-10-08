using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Notification
    {
        public int NotificationId { get; set; }

        [Display(Name = "User")]
        public int UserId { get; set; }

        [Display(Name = "Title")]
        public string Title { get; set; }

        [Display(Name = "Message")]
        public string Message { get; set; }

        [Display(Name = "Type")]
        public string Type { get; set; }

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
