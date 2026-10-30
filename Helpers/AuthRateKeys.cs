namespace COMS_MVC.Helpers
{
    /// <summary>
    /// Partition key for the "auth" rate-limit policy: remote IP + submitted
    /// username/email (case-insensitive) so one source of credential stuffing
    /// stays cheap without locking out a shared office IP that logs different
    /// accounts in. Falls back to IP-only when no usable identifier exists.
    /// </summary>
    public static class AuthRateKeys
    {
        public static string KeyFor(HttpContext context)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var user = string.Empty;
            try
            {
                if (context.Request.HasFormContentType)
                {
                    var form = context.Request.Form;
                    if (form.TryGetValue("Username", out var username))
                        user = username.ToString();
                    else if (form.TryGetValue("Email", out var email))
                        user = email.ToString();
                }
                if (string.IsNullOrWhiteSpace(user))
                    user = context.User?.Identity?.Name ?? string.Empty;
            }
            catch
            {
                // Unreadable/malformed body: degrade to IP-only keying.
            }

            user = user.Trim().ToUpperInvariant();
            if (user.Length > 64)
                user = user[..64];

            return $"{ip}|{user}";
        }
    }
}
