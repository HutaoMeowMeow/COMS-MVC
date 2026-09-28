using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Options for the Resend transactional email API.
    /// Bind from appsettings.json -> "Email". Never commit a real ApiKey.
    /// Prefer user-secrets / env vars in production.
    /// </summary>
    public sealed class ResendOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string SenderEmail { get; set; } = string.Empty;
        public string SenderName { get; set; } = "COMS";
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
    }

    /// <summary>
    /// Resend implementation over plain HttpClient (no extra SDK package required).
    /// Docs: POST https://api.resend.com/emails with "Authorization: Bearer &lt;ApiKey&gt;".
    /// </summary>
    public sealed class ResendEmailService : IEmailService
    {
        private readonly HttpClient _http;
        private readonly ResendOptions _options;
        private readonly ILogger<ResendEmailService> _logger;

        public ResendEmailService(
            HttpClient http,
            IOptions<ResendOptions> options,
            ILogger<ResendEmailService> logger)
        {
            _http = http;
            _options = options.Value;
            _logger = logger;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !string.IsNullOrWhiteSpace(_options.SenderEmail);

        public async Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) throw new ArgumentException("Recipient required.", nameof(toEmail));

            if (!IsConfigured)
            {
                // Dev fallback: log instead of throwing so Forgot Password still works locally.
                _logger.LogWarning(
                    "Email NOT sent (Resend not configured). To: {To} Subject: {Subject}",
                    toEmail, subject);
                return;
            }

            var payload = new ResendSendRequest(
                From: $"{_options.SenderName} <{_options.SenderEmail}>",
                To: new[] { toEmail },
                Subject: subject,
                Html: htmlBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Content = JsonContent.Create(payload);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                // Network/DNS failure — never include the API key.
                _logger.LogError(ex, "Resend request failed (network) sending to {To}", toEmail);
                throw new EmailServiceException("Email provider is temporarily unavailable. Please try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // Read a truncated, sanitized error body. Resend never echoes our key.
                var body = await SafeReadBodyAsync(response, ct);
                _logger.LogError(
                    "Resend API error {Status} sending to {To}: {Body}",
                    (int)response.StatusCode, toEmail, body);
                throw new EmailServiceException("Email provider rejected the request. Please try again later.");
            }

            _logger.LogInformation("Resend email sent to {To} with subject {Subject}", toEmail, subject);
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

        private sealed record ResendSendRequest(
            [property: JsonPropertyName("from")] string From,
            [property: JsonPropertyName("to")] string[] To,
            [property: JsonPropertyName("subject")] string Subject,
            [property: JsonPropertyName("html")] string Html);
    }
}
