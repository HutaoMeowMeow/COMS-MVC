using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class SensorReading
    {
        public int SensorReadingId { get; set; }

        [Display(Name = "Sensor")]
        public int SensorId { get; set; }

        [Display(Name = "Canal")]
        public int CanalId { get; set; }

        [Display(Name = "Water Level (m)")]
        public double WaterLevel { get; set; }

        [Display(Name = "Flow Rate (m³/s)")]
        public double FlowRate { get; set; }

        [Display(Name = "Debris Level (%)")]
        public double DebrisLevel { get; set; }

        [Display(Name = "Turbidity (NTU)")]
        public double Turbidity { get; set; }

        [Display(Name = "Temperature (°C)")]
        public double Temperature { get; set; }

        [Display(Name = "Recorded At")]
        public DateTime RecordedAt { get; set; }

        [Display(Name = "Simulated")]
        public bool IsSimulated { get; set; } = true;

        public Sensor? Sensor { get; set; }

        public Canal? Canal { get; set; }

        public ICollection<ObstructionAlert> Alerts { get; set; } = new List<ObstructionAlert>();
    }
}
