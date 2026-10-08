using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Canal
    {
        public int CanalId { get; set; }

        [Required(ErrorMessage = "Canal name is required")]
        [StringLength(150, ErrorMessage = "Canal name cannot exceed 150 characters")]
        [Display(Name = "Canal Name")]
        public string CanalName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Location is required")]
        [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters")]
        public string Location { get; set; } = string.Empty;

        [Required(ErrorMessage = "Barangay is required")]
        [StringLength(100, ErrorMessage = "Barangay cannot exceed 100 characters")]
        public string Barangay { get; set; } = string.Empty;

        [Required(ErrorMessage = "City/Municipality is required")]
        [StringLength(100, ErrorMessage = "City cannot exceed 100 characters")]
        [Display(Name = "City / Municipality")]
        public string City { get; set; } = string.Empty;

        [Display(Name = "Latitude")]
        [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90")]
        public double Latitude { get; set; }

        [Display(Name = "Longitude")]
        [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180")]
        public double Longitude { get; set; }

        [Display(Name = "Length (m)")]
        [Range(0, 100000, ErrorMessage = "Length must be 0 or greater")]
        public double Length { get; set; }

        [Display(Name = "Width (m)")]
        [Range(0, 10000, ErrorMessage = "Width must be 0 or greater")]
        public double Width { get; set; }

        [Display(Name = "Depth (m)")]
        [Range(0, 1000, ErrorMessage = "Depth must be 0 or greater")]
        public double Depth { get; set; }

        [Display(Name = "Normal Water Level (m)")]
        [Range(0, 1000, ErrorMessage = "Normal water level must be 0 or greater")]
        public double NormalWaterLevel { get; set; }

        [Display(Name = "Warning Water Level (m)")]
        [Range(0, 1000, ErrorMessage = "Warning water level must be 0 or greater")]
        public double WarningWaterLevel { get; set; }

        [Display(Name = "Critical Water Level (m)")]
        [Range(0, 1000, ErrorMessage = "Critical water level must be 0 or greater")]
        public double CriticalWaterLevel { get; set; }

        [Display(Name = "Status")]
        public string Status { get; set; } = "Normal";

        [Display(Name = "Created Date")]
        public DateTime CreatedAt { get; set; }

        public ICollection<Sensor> Sensors { get; set; } = new List<Sensor>();

        public ICollection<SensorReading> SensorReadings { get; set; } = new List<SensorReading>();

        public ICollection<ObstructionAlert> Alerts { get; set; } = new List<ObstructionAlert>();

        public ICollection<CommunityReport> CommunityReports { get; set; } = new List<CommunityReport>();

        public ICollection<FloodRiskAssessment> FloodRiskAssessments { get; set; } = new List<FloodRiskAssessment>();
    }
}
