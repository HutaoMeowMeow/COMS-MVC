using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Ticket-level moderation for the Customer Service "Report a Problem"
    /// form. Delegates textual spam / COMS-relevance analysis to
    /// <see cref="ITextModerationService"/> and adds category-specific
    /// rules (e.g. "Other" requires an OtherConcern detail).
    ///
    /// No external API is called; all heuristics are local and configurable
    /// via appsettings.json ("TextModeration").
    /// </summary>
    public interface ITicketModerationService
    {
        Task<TicketModerationResult> ModerateAsync(
            string category, string subject, string? description,
            string? otherConcern = null, int? subjectMaxLength = null,
            int? descriptionMaxLength = null);
    }

    public sealed class TicketModerationResult
    {
        /// <summary>Accept / Reject / Uncertain (send to staff review).</summary>
        public string Status { get; init; } = "Accept";

        public List<string> Errors { get; init; } = new();

        /// <summary>Suggested SupportTicket.Status: "Open" or "Under Review".</summary>
        public string SuggestedStatus { get; init; } = "Open";

        public bool IsRejected => Status == "Reject";
        public bool IsUncertain => Status == "Uncertain";
    }

    public sealed class TicketModerationService : ITicketModerationService
    {
        private readonly ITextModerationService _textModeration;
        private readonly ILogger<TicketModerationService> _logger;

        public TicketModerationService(ITextModerationService textModeration,
            ILogger<TicketModerationService> logger)
        {
            _textModeration = textModeration;
            _logger = logger;
        }

        public async Task<TicketModerationResult> ModerateAsync(
            string category, string subject, string? description,
            string? otherConcern = null, int? subjectMaxLength = null,
            int? descriptionMaxLength = null)
        {
            var errors = new List<string>();
            var isOther = category == "Other";

            // ---- Category must be a known value ----
            if (!IsAllowedCategory(category))
            {
                errors.Add("Please select a valid category.");
            }

            // ---- Subject presence / length ----
            var trimmedSubject = (subject ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedSubject))
            {
                errors.Add("Subject is required.");
            }
            else if (subjectMaxLength.HasValue && trimmedSubject.Length > subjectMaxLength.Value)
            {
                errors.Add($"Subject cannot exceed {subjectMaxLength} characters.");
            }

            // ---- Description presence / length ----
            // For non-Other categories the description is required.
            // For "Other" the OtherConcern field serves as the description.
            var trimmedDescription = (description ?? string.Empty).Trim();
            if (!isOther)
            {
                if (string.IsNullOrWhiteSpace(trimmedDescription))
                {
                    errors.Add("Description is required.");
                }
                else if (descriptionMaxLength.HasValue && trimmedDescription.Length > descriptionMaxLength.Value)
                {
                    errors.Add($"Description cannot exceed {descriptionMaxLength} characters.");
                }
            }
            else
            {
                // Other Concern serves as the description for the "Other" category.
                if (descriptionMaxLength.HasValue && trimmedDescription.Length > descriptionMaxLength.Value)
                {
                    errors.Add($"Description cannot exceed {descriptionMaxLength} characters.");
                }
            }

            // ---- "Other" category requires OtherConcern ----
            var trimmedOther = (otherConcern ?? string.Empty).Trim();
            if (isOther)
            {
                if (string.IsNullOrWhiteSpace(trimmedOther))
                {
                    errors.Add("Please describe your concern in the 'Other concern' field.");
                }
                else if (trimmedOther.Length > 500)
                {
                    errors.Add("Other concern cannot exceed 500 characters.");
                }
            }

            if (errors.Any())
            {
                return new TicketModerationResult
                {
                    Status = "Reject",
                    Errors = errors,
                    SuggestedStatus = "Open"
                };
            }

            // ---- Delegate spam / relevance analysis to text moderation ----
            // For "Other" category the OtherConcern text is the effective
            // description; for all others the Description field is used.
            var effectiveDescription = isOther ? trimmedOther : trimmedDescription;
            var analysis = _textModeration.AnalyzeText(trimmedSubject, effectiveDescription);

            if (analysis.IsRejected)
            {
                _logger.LogInformation("Ticket moderation rejected content. Category={Category}, Reasons={Reasons}",
                    category, string.Join("; ", analysis.Reasons));
                return new TicketModerationResult
                {
                    Status = "Reject",
                    Errors = analysis.Reasons,
                    SuggestedStatus = analysis.SuggestedStatus
                };
            }

            if (analysis.IsUncertain)
            {
                _logger.LogInformation("Ticket moderation marked content as uncertain for review. Category={Category}, Reasons={Reasons}",
                    category, string.Join("; ", analysis.Reasons));
                return new TicketModerationResult
                {
                    Status = "Uncertain",
                    Errors = analysis.Reasons,
                    SuggestedStatus = "Under Review"
                };
            }

            return new TicketModerationResult
            {
                Status = "Accept",
                Errors = analysis.Reasons.Where(IsSpamSignal).ToList(),
                SuggestedStatus = analysis.SuggestedStatus
            };
        }

        private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "Account", "Report", "Technical Issue", "General Inquiry", "Other"
        };

        private static bool IsAllowedCategory(string? category) =>
            !string.IsNullOrWhiteSpace(category) && AllowedCategories.Contains(category.Trim());

        private static bool IsSpamSignal(string signal) =>
            signal.StartsWith("Repeated character", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Repeated numeric", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Repeated word", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Keyboard mashing", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Keyboard row pattern", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Gibberish", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("High symbol ratio", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("All-caps title", StringComparison.OrdinalIgnoreCase);
    }
}
