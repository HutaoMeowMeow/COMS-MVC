namespace COMS_MVC.Models
{
    public class DashboardViewModel
    {
        public int TotalCanals { get; set; }

        public int TotalSensors { get; set; }

        public int ActiveAlerts { get; set; }

        public int PendingReports { get; set; }

        /// <summary>Reports owned by the current user (Resident scope).</summary>
        public int MyReportsCount { get; set; }

        /// <summary>Pending/Under Review reports owned by the current user.</summary>
        public int MyPendingReportsCount { get; set; }

        public int HighRiskCanals { get; set; }

        public int RegisteredUsers { get; set; }

        public int OnlineSensors { get; set; }

        public int OfflineSensors { get; set; }

        public List<ObstructionAlert> RecentAlerts { get; set; } = new List<ObstructionAlert>();

        public List<CommunityReport> RecentReports { get; set; } = new List<CommunityReport>();

        public List<FloodRiskAssessment> RecentRiskAssessments { get; set; } = new List<FloodRiskAssessment>();

        public List<Canal> CanalStatusOverview { get; set; } = new List<Canal>();

        public List<Sensor> SensorStatus { get; set; } = new List<Sensor>();

        public List<SensorReading> LatestReadings { get; set; } = new List<SensorReading>();

        public List<Announcement> Announcements { get; set; } = new List<Announcement>();

        public int UnreadNotifications { get; set; }

        public string CurrentRole { get; set; }

        public string UserName { get; set; }

        public string UserBarangay { get; set; }
    }
}
