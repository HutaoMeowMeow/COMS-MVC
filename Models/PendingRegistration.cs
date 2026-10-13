using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    /// <summary>
    /// Temporary registration record. Holds everything needed to create the real
    /// <see cref="ApplicationUser"/> ONLY after the email OTP is verified.
    /// Pending rows never appear in the Admin user list (that queries AspNetUsers).
    /// Abandoned rows expire 24h after creation and are cleaned up automatically.
    /// </summary>
    public class PendingRegistration
    {
        public int PendingRegistrationId { get; set; }

        [Required]
        [StringLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(256)]
        public string NormalizedEmail { get; set; } = string.Empty;

        [Required]
        [StringLength(256)]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [StringLength(256)]
        public string NormalizedUserName { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string FullName { get; set; } = string.Empty;

        [StringLength(30)]
        public string? PhoneNumber { get; set; }

        [StringLength(100)]
        public string? Barangay { get; set; }

        [StringLength(100)]
        public string? City { get; set; }

        [StringLength(250)]
        public string? Address { get; set; }

        [Required]
        [StringLength(50)]
        public string Role { get; set; } = "Resident";

        /// <summary>Identity password hash. Plaintext passwords are never stored.</summary>
        [Required]
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>SHA-256 hash of the 6-digit OTP. Never the plain code.</summary>
        public string? OtpHash { get; set; }

        public DateTime? OtpExpiresAtUtc { get; set; }

        public int FailedAttempts { get; set; }

        public DateTime? LastSentAtUtc { get; set; }

        public int RequestCount { get; set; }

        public DateTime? RequestWindowStartUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        /// <summary>Pending rows older than this are deleted. Never touches real users.</summary>
        public DateTime ExpiresAtUtc { get; set; }
    }
}
