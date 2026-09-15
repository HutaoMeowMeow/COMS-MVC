using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Canal
    {
        public int CanalId { get; set; }

        [Required(ErrorMessage = "Canal name is required")]
        [Display(Name = "Canal Name")]
        public string CanalName { get; set; }

        [Required(ErrorMessage = "Location is required")]
        public string Location { get; set; }

        [Required(ErrorMessage = "Barangay is required")]
        public string Barangay { get; set; }

        [Required(ErrorMessage = "City/Municipality is required")]
        [Display(Name = "City / Municipality")]
        public string City { get; set; }

        [Display(Name = "Latitude")]
        public double Latitude { get; set; }

        [Display(Name = "Longitude")]
        public double Longitude { get; set; }

        [Display(Name = "Length (m)")]
        public double Length { get; set; }

        [Display(Name = "Width (m)")]
        public double Width { get; set; }

        [Display(Name = "Depth (m)")]
        public double Depth { get; set; }

        [Display(Name = "Normal Water Level (m)")]
        public double NormalWaterLevel { get; set; }

        [Display(Name = "Warning Water Level (m)")]
        public double WarningWaterLevel { get; set; }

        [Display(Name = "Critical Water Level (m)")]
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
