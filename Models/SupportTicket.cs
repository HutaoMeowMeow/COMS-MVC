namespace COMS_MVC.Models
{
    /// <summary>
    /// Customer-service / help-desk ticket. Separate from CommunityReport
    /// (canal problems): this covers account, technical and general inquiries.
    /// Owner is always taken from the server-side identity, never from input.
    /// </summary>
    public class SupportTicket
    {
        public int SupportTicketId { get; set; }

        [Display(Name = "Resident")]
        public int UserId { get; set; }

        [Required(ErrorMessage = "Subject is required")]
        [StringLength(150, ErrorMessage = "Subject cannot exceed 150 characters")]
        public string Subject { get; set; } = string.Empty;

        [Required(ErrorMessage = "Description is required")]
        [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
        public string Description { get; set; } = string.Empty;

        [Display(Name = "Category")]
        public string Category { get; set; } = "General Inquiry";

        [Display(Name = "Priority")]
        public string Priority { get; set; } = "Normal";

        [Display(Name = "Status")]
        public string Status { get; set; } = "Open";

        [Display(Name = "Assigned To")]
        public int? AssignedToUserId { get; set; }

        [Display(Name = "Created Date")]
        public DateTime CreatedAt { get; set; }

        [Display(Name = "Updated Date")]
        public DateTime UpdatedAt { get; set; }

        [Display(Name = "Closed Date")]
        public DateTime? ClosedAt { get; set; }

        public ApplicationUser? User { get; set; }

        public ApplicationUser? AssignedToUser { get; set; }

        public ICollection<SupportMessage> Messages { get; set; } = new List<SupportMessage>();
    }
}
