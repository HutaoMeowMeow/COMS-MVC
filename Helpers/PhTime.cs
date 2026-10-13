namespace COMS_MVC.Helpers
{
    /// <summary>
    /// Philippine display time (UTC+8, no DST) + relative labels.
    /// All timestamps are stored as UTC DateTime; Npgsql returns them with
    /// Kind=Unspecified, so they are treated as UTC explicitly.
    /// </summary>
    public static class PhTime
    {
        /// <summary>Philippines is UTC+8 year-round (no DST, no TZ-DB needed).</summary>
        public static readonly TimeSpan PhOffset = TimeSpan.FromHours(8);

        public static DateTime AsUtc(DateTime dt) =>
            dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        public static DateTimeOffset ToPh(DateTime utc) =>
            new DateTimeOffset(AsUtc(utc), TimeSpan.Zero).ToOffset(PhOffset);

        /// <summary>Current Philippine wall-clock hour (for greetings). No server-tz dependence.</summary>
        public static int CurrentPhHour() => (DateTime.UtcNow + PhOffset).Hour;

        /// <summary>e.g. "Oct 12, 2026, 02:30 PM" in Philippine time.</summary>
        public static string ToPhString(DateTime utc, string format = "MMM dd, yyyy, hh:mm tt") =>
            ToPh(utc).ToString(format);

        /// <summary>e.g. "Oct 12, 2026" in Philippine time (date-only display).</summary>
        public static string ToPhDateString(DateTime utc) => ToPh(utc).ToString("MMM dd, yyyy");

        /// <summary>
        /// Human-readable relative label. Scale: Just now (&lt;1 min),
        /// X minutes, X hours, X days (1-6), then "MMM dd, yyyy" in
        /// Philippine time. Future timestamps clamp to "Just now" (never
        /// negative). Both inputs are treated as UTC (see AsUtc), so server
        /// timezone — including Railway hosts — cannot skew the comparison.
        /// </summary>
        public static string ToRelative(DateTime utc, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var diff = AsUtc(now) - AsUtc(utc);
            if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
            if (diff.TotalSeconds < 60) return "Just now";
            if (diff.TotalMinutes < 60)
            {
                var m = (int)diff.TotalMinutes;
                return m == 1 ? "1 minute ago" : $"{m} minutes ago";
            }
            if (diff.TotalHours < 24)
            {
                var h = (int)diff.TotalHours;
                return h == 1 ? "1 hour ago" : $"{h} hours ago";
            }
            if (diff.TotalDays < 7)
            {
                var d = (int)diff.TotalDays;
                return d == 1 ? "1 day ago" : $"{d} days ago";
            }
            return ToPhDateString(utc);
        }
    }
}
