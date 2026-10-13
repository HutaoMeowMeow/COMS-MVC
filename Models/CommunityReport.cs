using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class CommunityReport
    {
        public int CommunityReportId { get; set; }

        [Display(Name = "Resident")]
        public int UserId { get; set; }

        [Display(Name = "Canal")]
        public int? CanalId { get; set; }

        [Display(Name = "Report Type")]
        [Required(ErrorMessage = "Report type is required")]
        [StringLength(100, ErrorMessage = "Report type cannot exceed 100 characters")]
        public string ReportType { get; set; } = string.Empty;

        [Required(ErrorMessage = "Title is required")]
        [StringLength(150, ErrorMessage = "Title cannot exceed 150 characters")]
        [Display(Name = "Title")]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "Description")]
        [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
        public string Description { get; set; } = string.Empty;

        [Display(Name = "Photo")]
        [StringLength(500, ErrorMessage = "Photo path is too long")]
        public string? PhotoPath { get; set; }

        [Display(Name = "Location")]
        [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters")]
        public string? Location { get; set; }

        [Display(Name = "Latitude")]
        [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90")]
        public double? Latitude { get; set; }

        [Display(Name = "Longitude")]
        [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180")]
        public double? Longitude { get; set; }

        [Display(Name = "Status")]
        [Required(ErrorMessage = "Status is required")]
        [StringLength(50, ErrorMessage = "Status cannot exceed 50 characters")]
        public string Status { get; set; } = "Pending";

        /// <summary>
        /// Photo authenticity assessment: Pending / Authentic / Suspicious /
        /// LikelyManipulated / UnableToDetermine / Failed. Null when no photo
        /// was uploaded or verification is disabled. Advisory only — never
        /// auto-rejects a report.
        /// </summary>
        [Display(Name = "Image Verification")]
        [StringLength(30)]
        public string? ImageVerificationStatus { get; set; }

        /// <summary>0–100 confidence of the assessment, when available.</summary>
        [Range(0, 100)]
        public int? ImageVerificationConfidence { get; set; }

        [StringLength(500)]
        public string? ImageVerificationReason { get; set; }

        public DateTime? ImageVerificationAnalyzedAt { get; set; }

        /// <summary>
        /// Photo content-relevance assessment: Relevant / Irrelevant /
        /// Uncertain, decided synchronously before the report is saved.
        /// Null when no photo was uploaded or relevance checking is disabled.
        /// </summary>
        [Display(Name = "Photo Relevance")]
        [StringLength(30)]
        public string? ImageRelevanceStatus { get; set; }

        /// <summary>0–80 confidence of the relevance assessment, when available.</summary>
        [Range(0, 100)]
        public int? ImageRelevanceConfidence { get; set; }

        [StringLength(500)]
        public string? ImageRelevanceReason { get; set; }

        /// <summary>
        /// Best-matching report category suggested by the photo evidence, if any.
        /// Advisory only — the resident's selected report type is never changed.
        /// </summary>
        [Display(Name = "Suggested Category")]
        [StringLength(100)]
        public string? ImageSuggestedCategory { get; set; }

        [Display(Name = "Created Date")]
        public DateTime CreatedAt { get; set; }

        [Display(Name = "Updated Date")]
        public DateTime UpdatedAt { get; set; }

        public ApplicationUser? User { get; set; }

        public Canal? Canal { get; set; }
    }
}
