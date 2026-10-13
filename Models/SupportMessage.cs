namespace COMS_MVC.Models
{
    /// <summary>
    /// One chat/conversation line inside a support ticket.
    /// IsInternal = staff-only note, never exposed to the resident.
    /// </summary>
    public class SupportMessage
    {
        public int SupportMessageId { get; set; }

        public int SupportTicketId { get; set; }

        [Display(Name = "Sender")]
        public int SenderUserId { get; set; }

        [Required(ErrorMessage = "Message is required")]
        [StringLength(2000, ErrorMessage = "Message cannot exceed 2000 characters")]
        public string Message { get; set; } = string.Empty;

        [Display(Name = "Sent At")]
        public DateTime SentAt { get; set; }

        [Display(Name = "Is Read")]
        public bool IsRead { get; set; } = false;

        /// <summary>Staff-only internal note. Hidden from the resident.</summary>
        [Display(Name = "Internal Note")]
        public bool IsInternal { get; set; } = false;

        public SupportTicket? Ticket { get; set; }

        public ApplicationUser? Sender { get; set; }
    }
}
