using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Announcement
    {
        public int AnnouncementId { get; set; }

        [Required(ErrorMessage = "Title is required")]
        [Display(Name = "Title")]
        public string Title { get; set; }

        [Display(Name = "Content")]
        public string Content { get; set; }

        [Display(Name = "Image")]
        public string? ImagePath { get; set; }

        [Display(Name = "Target Audience")]
        public string TargetAudience { get; set; }

        [Display(Name = "Location")]
        public string? Location { get; set; }

        [Display(Name = "Posted By")]
        public int PostedByUserId { get; set; }

        [Display(Name = "Created At")]
        public DateTime CreatedAt { get; set; }

        public ApplicationUser? PostedBy { get; set; }
    }
}
