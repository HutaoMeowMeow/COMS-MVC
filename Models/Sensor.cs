using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Sensor
    {
        public int SensorId { get; set; }

        [Required(ErrorMessage = "Sensor code is required")]
        [StringLength(50, ErrorMessage = "Sensor code cannot exceed 50 characters")]
        [Display(Name = "Sensor Code")]
        public string SensorCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Sensor type is required")]
        [StringLength(50, ErrorMessage = "Sensor type cannot exceed 50 characters")]
        [Display(Name = "Sensor Type")]
        public string SensorType { get; set; } = string.Empty;

        [Display(Name = "Canal")]
        [Range(1, int.MaxValue, ErrorMessage = "Please select a canal")]
        public int CanalId { get; set; }

        [Display(Name = "Latitude")]
        [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90")]
        public double Latitude { get; set; }

        [Display(Name = "Longitude")]
        [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180")]
        public double Longitude { get; set; }

        [Display(Name = "Status")]
        public string Status { get; set; } = "Online";

        [Display(Name = "Last Reading")]
        public DateTime LastReading { get; set; }

        [Display(Name = "Last Communication")]
        public DateTime LastCommunication { get; set; }

        public Canal? Canal { get; set; }

        public ICollection<SensorReading> SensorReadings { get; set; } = new List<SensorReading>();
    }
}
