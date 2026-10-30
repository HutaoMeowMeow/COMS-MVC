namespace COMS_MVC.Models
{
    public class TrustedDevice
    {
        public int TrustedDeviceId { get; set; }

        public int UserId { get; set; }

        public string DeviceName { get; set; } = string.Empty;

        public string TokenHash { get; set; } = string.Empty;

        public string? UserAgent { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public DateTime ExpiresAtUtc { get; set; }

        public DateTime LastSeenAtUtc { get; set; }

        public ApplicationUser? User { get; set; }
    }
}
