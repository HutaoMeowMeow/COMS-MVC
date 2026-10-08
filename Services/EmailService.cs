using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Options for the Brevo transactional email API.
    /// Bind from appsettings.json -&gt; "Email". Never commit a real ApiKey.
    /// Prefer user-secrets / env vars in production:
    ///   dotnet user-secrets set "Email:ApiKey" "xkeysib_xxx"
    ///   dotnet user-secrets set "Email:SenderEmail" "no-reply@yourdomain.com"
    /// Brevo docs: POST https://api.brevo.com/v3/smtp/email with header "api-key".
    /// </summary>
    public class BrevoOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string SenderEmail { get; set; } = string.Empty;
        public string SenderName { get; set; } = "COMS";
    }

    /// <summary>
    /// Back-compat alias: the "Email" config section keeps the same shape,
    /// so existing user-secrets / env vars keep working after the Resend -&gt; Brevo move.
    /// </summary>
    [Obsolete("Use BrevoOptions. Kept so old DI references fail loudly instead of silently.")]
    public sealed class ResendOptions : BrevoOptions
    {
    }

    public sealed class EmailServiceException : Exception
    {
        public EmailServiceException(string message) : base(message) { }
    }

    public interface IEmailService
    {
        Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
        Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken ct = default);
        Task SendPasswordResetCodeAsync(string toEmail, string code, CancellationToken ct = default);
        Task SendOtpEmailAsync(string toEmail, string otpCode, CancellationToken ct = default);
    }

    /// <summary>
    /// Brevo implementation over plain HttpClient (no extra SDK package required).
    /// All API communication stays server-side; the key travels only as the "api-key" header.
    /// </summary>
    public sealed class BrevoEmailService : IEmailService
    {
        private readonly HttpClient _http;
        private readonly BrevoOptions _options;
        private readonly ILogger<BrevoEmailService> _logger;

        public BrevoEmailService(
            HttpClient http,
            IOptions<BrevoOptions> options,
            ILogger<BrevoEmailService> logger)
        {
            _http = http;
            _options = options.Value;
            _logger = logger;

            // Startup diagnostic (never logs the key itself): Brevo API keys start
            // with "xkeysib-". Anything else (e.g. a Resend "re_" key left over from
            // the old provider) will get HTTP 401 from api.brevo.com.
            _logger.LogInformation(
                "Brevo API key configured: {HasKey}, Brevo sender configured: {HasSender}",
                !string.IsNullOrWhiteSpace(_options.ApiKey),
                !string.IsNullOrWhiteSpace(_options.SenderEmail));
            if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
                !_options.ApiKey.StartsWith("xkeysib-", StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Email:ApiKey does not look like a Brevo key (expected prefix 'xkeysib-'). " +
                    "Brevo will reject mail sends until a valid Brevo key is configured via user-secrets or env vars.");
            }
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !string.IsNullOrWhiteSpace(_options.SenderEmail);

        public async Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) throw new ArgumentException("Recipient required.", nameof(toEmail));

            if (!IsConfigured)
            {
                // Dev fallback: log instead of throwing so auth flows still work locally.
                _logger.LogWarning(
                    "Email NOT sent (Brevo not configured). To: {To} Subject: {Subject}",
                    toEmail, subject);
                return;
            }

            var payload = new BrevoSendRequest(
                Sender: new BrevoSender(Name: _options.SenderName, Email: _options.SenderEmail),
                To: new[] { new BrevoRecipient(Email: toEmail) },
                Subject: subject,
                HtmlContent: htmlBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, "smtp/email");
            request.Headers.Add("api-key", _options.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = JsonContent.Create(payload);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                // Network/DNS failure — never include the API key.
                _logger.LogError(ex, "Brevo request failed (network) sending to {To}", toEmail);
                throw new EmailServiceException("Email provider is temporarily unavailable. Please try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, ct);
                _logger.LogError(
                    "Brevo API error {Status} sending to {To}: {Body}",
                    (int)response.StatusCode, toEmail, body);
                throw new EmailServiceException("Email provider rejected the request. Please try again later.");
            }

            _logger.LogInformation("Brevo email sent to {To} with subject {Subject}", toEmail, subject);
        }

        public Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken ct = default)
        {
            var safeLink = WebUtility.HtmlEncode(resetLink);
            var html = $"""
                <div style="font-family:Arial,sans-serif;max-width:560px;margin:0 auto;">
                  <h2>Reset your COMS password</h2>
                  <p>You requested a password reset for your COMS account.</p>
                  <p style="margin:24px 0;">
                    <a href="{safeLink}" style="display:inline-block;padding:12px 24px;background:#2b6cb0;color:#fff;text-decoration:none;border-radius:8px;">Reset password</a>
                  </p>
                  <p style="color:#555;font-size:13px;">This link expires in 1 hour. If the button doesn't work, copy this URL into your browser:</p>
                  <p style="font-size:12px;word-break:break-all;color:#555;">{safeLink}</p>
                  <p style="color:#555;font-size:13px;">If you did not request this, you can safely ignore this email.</p>
                  <p>— Canal Obstruction Maintenance System</p>
                </div>
                """;
            return SendEmailAsync(toEmail, "Reset your COMS password", html, ct);
        }

        public Task SendPasswordResetCodeAsync(string toEmail, string code, CancellationToken ct = default)
        {
            var safeCode = WebUtility.HtmlEncode(code);
            var html = $"""
                <div style="font-family:Arial,sans-serif;max-width:560px;margin:0 auto;">
                  <h2>Your COMS password reset code</h2>
                  <p>You requested a password reset for your COMS account.</p>
                  <p style="font-size:28px;font-weight:bold;letter-spacing:6px;">{safeCode}</p>
                  <p style="color:#555;font-size:13px;">This code expires in 10 minutes. Enter it on the Reset Password page along with your new password.</p>
                  <p style="color:#555;font-size:13px;">If you did not request this, you can safely ignore this email.</p>
                  <p>— Canal Obstruction Maintenance System</p>
                </div>
                """;
            return SendEmailAsync(toEmail, "Your COMS password reset code", html, ct);
        }

        public Task SendOtpEmailAsync(string toEmail, string otpCode, CancellationToken ct = default)
        {
            var safeCode = WebUtility.HtmlEncode(otpCode);
            var html = $"""
                <div style="font-family:Arial,sans-serif;max-width:560px;margin:0 auto;">
                  <h2 style="color:#1e3a5f;">Verify your COMS account</h2>
                  <p>Thank you for registering with the <strong>Canal Obstruction Maintenance System</strong>.</p>
                  <p>Enter this verification code to activate your account:</p>
                  <p style="font-size:32px;font-weight:bold;letter-spacing:8px;color:#1e3a5f;">{safeCode}</p>
                  <p style="color:#555;font-size:13px;">This code expires in 10 minutes and can only be used once.</p>
                  <p style="color:#555;font-size:13px;">If you did not create this account, you can safely ignore this email.</p>
                  <p>— Canal Obstruction Maintenance System</p>
                </div>
                """;
            return SendEmailAsync(toEmail, "Your COMS verification code", html, ct);
        }

        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return body.Length > 500 ? body[..500] : body;
            }
            catch
            {
                return "(unreadable error body)";
            }
        }

        private sealed record BrevoSender(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("email")] string Email);

        private sealed record BrevoRecipient(
            [property: JsonPropertyName("email")] string Email);

        private sealed record BrevoSendRequest(
            [property: JsonPropertyName("sender")] BrevoSender Sender,
            [property: JsonPropertyName("to")] BrevoRecipient[] To,
            [property: JsonPropertyName("subject")] string Subject,
            [property: JsonPropertyName("htmlContent")] string HtmlContent);
    }
}
