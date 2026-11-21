namespace COMS_MVC.Models
{
    /// <summary>Read-only row for the Canal Monitor page (Canals/Map).</summary>
    public class CanalMonitorItem
    {
        public int CanalId { get; set; }
        public string CanalName { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Barangay { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double NormalWaterLevel { get; set; }
        public double WarningWaterLevel { get; set; }
        public double CriticalWaterLevel { get; set; }
        public int SensorTotal { get; set; }
        public int SensorOnline { get; set; }
        public int ActiveAlertCount { get; set; }
        public double? LatestWaterLevel { get; set; }
        public DateTime? LatestReadingAt { get; set; }
        public string? LatestReadingText { get; set; }
        public List<CanalMonitorSensor> Sensors { get; set; } = new();
    }

    public class CanalMonitorSensor
    {
        public int SensorId { get; set; }
        public string SensorCode { get; set; } = string.Empty;
        public string SensorType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
