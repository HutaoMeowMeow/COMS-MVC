using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class ObstructionAlert
    {
        public int ObstructionAlertId { get; set; }

        [Display(Name = "Canal")]
        public int CanalId { get; set; }

        [Display(Name = "Alert Type")]
        public string AlertType { get; set; }

        [Display(Name = "Severity")]
        public string Severity { get; set; } = "Low";

        [Display(Name = "Title")]
        public string Title { get; set; }

        [Display(Name = "Description")]
        public string Description { get; set; }

        [Display(Name = "Obstruction Type")]
        public string ObstructionType { get; set; }

        [Display(Name = "Water Level (m)")]
        public double WaterLevel { get; set; }

        [Display(Name = "Sensor Reading")]
        public int? SensorReadingId { get; set; }

        [Display(Name = "Detected At")]
        public DateTime DetectedAt { get; set; }

        [Display(Name = "Status")]
        public string Status { get; set; } = "Active";

        [Display(Name = "Assigned To")]
        public int? AssignedToUserId { get; set; }

        [Display(Name = "Resolution Notes")]
        public string? ResolutionNotes { get; set; }

        [Display(Name = "Resolved At")]
        public DateTime? ResolvedAt { get; set; }

        public Canal? Canal { get; set; }

        public SensorReading? SensorReading { get; set; }

        public ApplicationUser? AssignedToUser { get; set; }
    }
}
