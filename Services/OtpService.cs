using System.Security.Cryptography;
using System.Text;

namespace COMS_MVC.Services
{
    public sealed class OtpResult
    {
        public bool Succeeded { get; init; }
        public string Error { get; init; } = string.Empty;

        public static OtpResult Ok() => new() { Succeeded = true };
        public static OtpResult Fail(string error) => new() { Succeeded = false, Error = error };
    }

    public interface IOtpService
    {
        /// <summary>Generate a fresh OTP for the pending registration, store only its hash, and email the plain code via Brevo.</summary>
        Task<OtpResult> IssueAsync(PendingRegistration pending);
        /// <summary>Validate a user-entered code. Single-use: clears the stored hash on success.</summary>
        Task<OtpResult> VerifyAsync(PendingRegistration pending, string code);
    }

    /// <summary>
    /// 6-digit email OTP for registration. Hash (SHA-256) stored on the pending
    /// registration row; the plain code only ever travels inside the Brevo email body.
    /// Brevo sending itself is untouched — only the account-creation timing changed:
    /// no ApplicationUser row exists until verification succeeds.
    /// Policy: 10-min code expiry, max 5 verify attempts, 60s resend cooldown,
    /// max 5 sends/hour. Pending rows expire 24h after creation.
    /// </summary>
    public sealed class OtpService : IOtpService
    {
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);
        public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);
        public const int MaxSendsPerWindow = 5;
        public const int MaxVerifyAttempts = 5;

        private readonly IEmailService _email;
        private readonly ILogger<OtpService> _logger;

        public OtpService(IEmailService email, ILogger<OtpService> logger)
        {
            _email = email;
            _logger = logger;
        }

        public async Task<OtpResult> IssueAsync(PendingRegistration pending)
        {
            var now = DateTime.UtcNow;

            // Hourly windowed rate limit.
            if (pending.RequestWindowStartUtc is null ||
                now - pending.RequestWindowStartUtc > RateLimitWindow)
            {
                pending.RequestWindowStartUtc = now;
                pending.RequestCount = 0;
            }

            if (pending.LastSentAtUtc is not null &&
                now - pending.LastSentAtUtc < ResendCooldown)
            {
                var wait = (int)Math.Ceiling((ResendCooldown - (now - pending.LastSentAtUtc.Value)).TotalSeconds);
                return OtpResult.Fail($"Please wait {wait} seconds before requesting a new code.");
            }

            if (pending.RequestCount >= MaxSendsPerWindow)
            {
                return OtpResult.Fail("Too many verification requests. Please try again later.");
            }

            var code = GenerateCode();
            pending.OtpHash = Hash(code, pending.Email);
            pending.OtpExpiresAtUtc = now.Add(CodeLifetime);
            pending.FailedAttempts = 0;
            pending.LastSentAtUtc = now;
            pending.RequestCount++;

            try
            {
                await _email.SendOtpEmailAsync(pending.Email, code);
            }
            catch (EmailServiceException ex)
            {
                // Roll back so no phantom code exists that was never emailed.
                pending.OtpHash = null;
                pending.OtpExpiresAtUtc = null;
                pending.FailedAttempts = 0;
                _logger.LogError(ex, "OTP email delivery failed via Brevo.");
                return OtpResult.Fail("We could not send the verification email. Please try again later.");
            }

            _logger.LogInformation("OTP issued for pending registration.");
            return OtpResult.Ok();
        }

        public Task<OtpResult> VerifyAsync(PendingRegistration pending, string code)
        {
            code = (code ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("-", string.Empty);

            if (string.IsNullOrWhiteSpace(pending.OtpHash) || pending.OtpExpiresAtUtc is null)
            {
                return Task.FromResult(OtpResult.Fail("No active verification code. Please request a new one."));
            }

            if (DateTime.UtcNow > pending.OtpExpiresAtUtc.Value)
            {
                return Task.FromResult(OtpResult.Fail("Verification code expired. Please request a new one."));
            }

            if (pending.FailedAttempts >= MaxVerifyAttempts)
            {
                return Task.FromResult(OtpResult.Fail("Too many incorrect attempts. Please request a new code."));
            }

            var candidate = Hash(code, pending.Email);
            if (!FixedTimeEquals(candidate, pending.OtpHash))
            {
                pending.FailedAttempts++;
                var remaining = MaxVerifyAttempts - pending.FailedAttempts;
                var msg = remaining > 0
                    ? $"Invalid verification code. {remaining} attempt(s) remaining."
                    : "Too many incorrect attempts. Please request a new code.";
                return Task.FromResult(OtpResult.Fail(msg));
            }

            // Single-use: clear immediately so it cannot be replayed.
            pending.OtpHash = null;
            pending.OtpExpiresAtUtc = null;
            pending.FailedAttempts = 0;
            return Task.FromResult(OtpResult.Ok());
        }

        internal static string GenerateCode()
        {
            // Cryptographically secure 6 digits, never "000000".
            Span<byte> buf = stackalloc byte[4];
            string code;
            do
            {
                RandomNumberGenerator.Fill(buf);
                var n = BitConverter.ToUInt32(buf) % 1_000_000;
                code = n.ToString("D6");
            } while (code == "000000");
            return code;
        }

        internal static string Hash(string code, string salt)
        {
            var input = $"coms-otp-v1|{salt.ToUpperInvariant()}|{code}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var ab = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(ab, bb);
        }
    }
}
