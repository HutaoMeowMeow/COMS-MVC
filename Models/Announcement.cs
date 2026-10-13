using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Announcement
    {
        public int AnnouncementId { get; set; }

        [Required(ErrorMessage = "Title is required")]
        [StringLength(150, ErrorMessage = "Title cannot exceed 150 characters")]
        [Display(Name = "Title")]
        public string Title { get; set; } = string.Empty;

        [Required(ErrorMessage = "Content is required")]
        [StringLength(5000, ErrorMessage = "Content cannot exceed 5000 characters")]
        [Display(Name = "Content")]
        public string Content { get; set; } = string.Empty;

        [Display(Name = "Image")]
        public string? ImagePath { get; set; }

        [Required(ErrorMessage = "Target audience is required")]
        [StringLength(50, ErrorMessage = "Target audience cannot exceed 50 characters")]
        [Display(Name = "Target Audience")]
        public string TargetAudience { get; set; } = "All";

        [Display(Name = "Location")]
        [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters")]
        public string? Location { get; set; }

        [Display(Name = "Posted By")]
        public int PostedByUserId { get; set; }

        [Display(Name = "Created At")]
        public DateTime CreatedAt { get; set; }

        public ApplicationUser? PostedBy { get; set; }
    }
}
