using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class Sensor
    {
        public int SensorId { get; set; }

        [Required(ErrorMessage = "Sensor code is required")]
        [Display(Name = "Sensor Code")]
        public string SensorCode { get; set; }

        [Display(Name = "Sensor Type")]
        public string SensorType { get; set; }

        [Display(Name = "Canal")]
        public int CanalId { get; set; }

        [Display(Name = "Latitude")]
        public double Latitude { get; set; }

        [Display(Name = "Longitude")]
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
