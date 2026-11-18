using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    /// <summary>
    /// Private, per-user working copy of the "Report a Canal Problem" form.
    /// At most one active draft exists per resident. The draft is deleted when
    /// the resident submits it (converted into a <see cref="CommunityReport"/>)
    /// or when the resident removes it. Ownership is always checked against the
    /// authenticated user id, never against a posted value.
    /// </summary>
    public class ReportDraft
    {
        public int ReportDraftId { get; set; }

        /// <summary>Owner. Unique: one active draft per resident.</summary>
        public int UserId { get; set; }

        public int? CanalId { get; set; }

        [StringLength(100)]
        public string ReportType { get; set; } = "Garbage / Debris";

        [StringLength(150)]
        public string Title { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(200)]
        public string? Location { get; set; }

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }

        /// <summary>
        /// Server-side temporary attachment, kept across browser refreshes.
        /// Moved to /uploads/reports only when the report is submitted.
        /// </summary>
        [StringLength(500)]
        public string? PhotoPath { get; set; }

        /// <summary>Original uploaded file name (display only).</summary>
        [StringLength(255)]
        public string? PhotoFileName { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public ApplicationUser? User { get; set; }

        public Canal? Canal { get; set; }
    }
}
