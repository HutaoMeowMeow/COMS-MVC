using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class CommunityReport
    {
        public int CommunityReportId { get; set; }

        [Display(Name = "Resident")]
        public int UserId { get; set; }

        [Display(Name = "Canal")]
        public int CanalId { get; set; }

        [Display(Name = "Report Type")]
        public string ReportType { get; set; }

        [Required(ErrorMessage = "Title is required")]
        [Display(Name = "Title")]
        public string Title { get; set; }

        [Display(Name = "Description")]
        public string Description { get; set; }

        [Display(Name = "Photo")]
        public string? PhotoPath { get; set; }

        [Display(Name = "Location")]
        public string? Location { get; set; }

        [Display(Name = "Latitude")]
        [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90")]
        public double? Latitude { get; set; }

        [Display(Name = "Longitude")]
        [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180")]
        public double? Longitude { get; set; }

        [Display(Name = "Status")]
        public string Status { get; set; } = "Pending";

        [Display(Name = "Created Date")]
        public DateTime CreatedAt { get; set; }

        [Display(Name = "Updated Date")]
        public DateTime UpdatedAt { get; set; }

        public ApplicationUser? User { get; set; }

        public Canal? Canal { get; set; }
    }
}
