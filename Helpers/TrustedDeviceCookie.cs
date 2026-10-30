using COMS_MVC.Services;

namespace COMS_MVC.Helpers
{
    /// <summary>
    /// Reads/writes the opaque trusted-device token cookie. The cookie value is
    /// a cryptographically random token; only its SHA-256 hash is stored on the
    /// server (see <see cref="Services.TrustedDeviceService"/>). A manually
    /// edited cookie value therefore cannot prove that 2FA was completed.
    /// </summary>
    public static class TrustedDeviceCookie
    {
        public const string Name = "COMS.TrustedDevice";

        public static string? Read(HttpContext context) =>
            context.Request.Cookies.TryGetValue(Name, out var token)
                && !string.IsNullOrWhiteSpace(token)
                ? token
                : null;

        public static void Write(HttpContext context, string token, int days)
        {
            var boundedDays = Math.Clamp(days, 1, TwoFactorOptions.MaxTrustedDeviceDays);
            context.Response.Cookies.Append(Name, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(boundedDays),
                MaxAge = TimeSpan.FromDays(boundedDays)
            });
        }

        public static void Clear(HttpContext context)
        {
            context.Response.Cookies.Delete(Name, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/"
            });
        }

        public static string NewToken()
        {
            Span<byte> bytes = stackalloc byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(bytes);
        }
    }
}

