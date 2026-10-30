namespace COMS_MVC.Helpers
{
    /// <summary>
    /// Canonical alert-severity colour mapping used by every severity
    /// indicator in the app: Critical = danger, High = warning,
    /// Medium = info, Low = success, unknown = neutral.
    /// </summary>
    public static class SeverityStyles
    {
        public static string BadgeClass(string? severity) => severity switch
        {
            "Critical" => "danger",
            "High" => "warning",
            "Medium" => "info",
            "Low" => "success",
            _ => "secondary"
        };

        public static string StatusBadgeClass(string? severity) => severity switch
        {
            "Critical" => "st-critical",
            "High" => "st-high",
            "Medium" => "st-medium",
            "Low" => "st-low-severity",
            _ => "st-low"
        };
    }
}
