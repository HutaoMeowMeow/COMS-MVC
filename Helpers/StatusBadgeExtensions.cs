using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Text;

namespace COMS_MVC.Helpers
{
    /// <summary>
    /// One server-rendered status badge used across every role, so the same
    /// status never renders with a different colour or shape on another page.
    /// </summary>
    public static class StatusBadgeExtensions
    {
        public static IHtmlContent StatusBadge(this IHtmlHelper html, string? status)
        {
            var value = (status ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                value = "Unknown";
            }
            var css = StatusClass(value);
            return new HtmlString(
                $"<span class=\"status-badge {css}\">{System.Net.WebUtility.HtmlEncode(value)}</span>");
        }

        public static IHtmlContent SeverityBadge(this IHtmlHelper html, string? severity)
        {
            var value = (severity ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                value = "Unknown";
            }
            var css = value.ToLowerInvariant() switch
            {
                "critical" => "st-critical",
                "high" => "st-high",
                "medium" => "st-medium",
                "low" => "st-low",
                _ => "st-pending"
            };
            return new HtmlString(
                $"<span class=\"status-badge {css}\">{System.Net.WebUtility.HtmlEncode(value)}</span>");
        }

        public static string StatusClass(string? status) => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "pending" => "st-pending",
            "under review" => "st-under-review",
            "verified" => "st-verified",
            "assigned" => "st-assigned",
            "in progress" => "st-in-progress",
            "acknowledged" => "st-under-review",
            "resolved" => "st-resolved",
            "closed" => "st-resolved",
            "rejected" => "st-rejected",
            "critical" => "st-critical",
            "high" => "st-high",
            "medium" => "st-medium",
            "low" => "st-low",
            "active" => "st-in-progress",
            "online" => "st-resolved",
            "offline" => "st-rejected",
            "maintenance" => "st-medium",
            "inactive" => "st-pending",
            "warning" => "st-high",
            "elevated" => "st-high",
            "normal" => "st-resolved",
            _ => "st-pending"
        };
    }
}


