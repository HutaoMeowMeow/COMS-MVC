using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace COMS_MVC.Services
{
    /// <summary>
    /// SMTP implementation of <see cref="IEmailService"/> (defined in EmailService.cs).
    /// Reads <see cref="EmailSettings"/> (defined in EmailSender.cs) via IOptions.
    /// Resolution order (ASP.NET Core default): appsettings.json provides fallback
    /// values, environment variables (EmailSettings__SenderEmail, etc.) overlay them
    /// when present. So it sends immediately from appsettings.json if env vars are unset.
    /// </summary>
    public sealed class SmtpEmailService : IEmailService
    {
        private readonly EmailSettings _settings;
        private readonly ILogger<SmtpEmailService> _logger;

        public SmtpEmailService(IOptions<EmailSettings> options, ILogger<SmtpEmailService> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_settings.SmtpServer) &&
            !string.IsNullOrWhiteSpace(_settings.SenderEmail) &&
            !string.IsNullOrWhiteSpace(_settings.Username) &&
            !string.IsNullOrWhiteSpace(_settings.Password);

        public async Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) throw new ArgumentException("Recipient required.", nameof(toEmail));

            if (!IsConfigured)
            {
                _logger.LogWarning(
                    "Email NOT sent (EmailSettings incomplete). Server={Server} Port={Port} Sender={Sender} HasUsername={HasUser} HasPassword={HasPass}. To: {To} Subject: {Subject}",
                    _settings.SmtpServer, _settings.SmtpPort, _settings.SenderEmail,
                    !string.IsNullOrWhiteSpace(_settings.Username),
                    !string.IsNullOrWhiteSpace(_settings.Password),
                    toEmail, subject);
                throw new EmailServiceException(
                    "Email is not configured. Fill appsettings.json EmailSettings or set EmailSettings__SenderEmail / Username / Password and restart.");
            }

            using var message = new MailMessage
            {
                From = new MailAddress(_settings.SenderEmail.Trim(), _settings.SenderName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(toEmail.Trim());

            // Gmail App Passwords are shown with spaces — SMTP rejects them with spaces.
            var username = (_settings.Username ?? string.Empty).Trim();
            var password = (_settings.Password ?? string.Empty).Replace(" ", string.Empty).Trim();

            // Gmail (Port 587 / STARTTLS). UseDefaultCredentials MUST precede Credentials.
            using var client = new SmtpClient(_settings.SmtpServer.Trim(), _settings.SmtpPort)
            {
                EnableSsl = true,
                UseDefaultCredentials = false, // MUST be set BEFORE setting Credentials
                Credentials = new NetworkCredential(username, password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 20000
            };

            try
            {
                await client.SendMailAsync(message, ct);
            }
            catch (SmtpFailedRecipientsException ex)
            {
                // Recipient rejected (mailbox unavailable, etc.). Never log credentials.
                foreach (SmtpFailedRecipientException inner in ex.InnerExceptions)
                {
                    _logger.LogError(inner,
                        "SMTP recipient failed. Server={Server} Port={Port} Sender={Sender} To={To} Status={Status} Response={Response}",
                        _settings.SmtpServer, _settings.SmtpPort, _settings.SenderEmail,
                        toEmail, inner.StatusCode, StripNewlines(inner.Message));
                }
                throw new EmailServiceException($"Recipient rejected ({ex.StatusCode}). Check the destination address.");
            }
            catch (SmtpException ex)
            {
                // Auth (5.7.8), TLS, or connection failure. StatusCode + message
                // distinguish them; Username/Password are never logged.
                // Google's reason is embedded in the thrown message (no credentials)
                // so the controller log alone is enough to diagnose.
                var googleReason = StripNewlines(ex.Message);
                _logger.LogError(ex,
                    "SMTP send failed. Server={Server} Port={Port} Sender={Sender} Ssl={Ssl} To={To} Status={Status} Message={Message} Inner={Inner}",
                    _settings.SmtpServer, _settings.SmtpPort, _settings.SenderEmail, _settings.EnableSsl,
                    toEmail, ex.StatusCode, googleReason, StripNewlines(ex.InnerException?.Message ?? "-"));
                throw new EmailServiceException(
                    $"SMTP {ex.StatusCode}: {googleReason}");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                // DNS / connection / firewall, e.g. smtp.gmail.com:587 unreachable.
                _logger.LogError(ex,
                    "SMTP socket error. Server={Server} Port={Port} To={To} SocketError={SocketError} Message={Message}",
                    _settings.SmtpServer, _settings.SmtpPort, toEmail, ex.SocketErrorCode, StripNewlines(ex.Message));
                throw new EmailServiceException("Cannot reach SMTP server. Check host/port/firewall and try again.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex,
                    "SMTP invalid operation (bad From/To format?). Sender={Sender} To={To} Message={Message}",
                    _settings.SenderEmail, toEmail, StripNewlines(ex.Message));
                throw new EmailServiceException("Email misconfigured (bad sender/recipient format).");
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex,
                    "SMTP address format error. Sender={Sender} To={To} Message={Message}",
                    _settings.SenderEmail, toEmail, StripNewlines(ex.Message));
                throw new EmailServiceException("Email misconfigured (bad address format).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SMTP unexpected error. Server={Server} Port={Port} Sender={Sender} To={To} Type={Type} Message={Message}",
                    _settings.SmtpServer, _settings.SmtpPort, _settings.SenderEmail,
                    toEmail, ex.GetType().Name, StripNewlines(ex.Message));
                throw new EmailServiceException("Email send failed unexpectedly. See server logs.");
            }

            _logger.LogInformation("SMTP email sent to {To} with subject {Subject}", toEmail, subject);
        }

        public async Task SendPasswordResetEmailAsync(string toEmail, string resetLink, CancellationToken ct = default)
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
            await SendEmailAsync(toEmail, "Reset your COMS password", html, ct);
            _logger.LogInformation("Password reset email sent to {Email}", toEmail);
        }

        public Task SendPasswordResetCodeAsync(string toEmail, string code, CancellationToken ct = default)
        {
            var safeCode = WebUtility.HtmlEncode(code);
            var html = $"""
                <div style="font-family:Arial,sans-serif;max-width:560px;margin:0 auto;">
                  <h2>Your COMS password reset code</h2>
                  <p>You requested a password reset for your COMS account.</p>
                  <p style="font-size:28px;font-weight:bold;letter-spacing:6px;">{safeCode}</p>
                  <p style="color:#555;font-size:13px;">This code expires in 10 minutes.</p>
                  <p style="color:#555;font-size:13px;">If you did not request this, you can safely ignore this email.</p>
                  <p>— Canal Obstruction Maintenance System</p>
                </div>
                """;
            return SendEmailAsync(toEmail, "Your COMS password reset code", html, ct);
        }

        private static string StripNewlines(string? value) =>
            string.IsNullOrEmpty(value)
                ? "-"
                : value.Replace('\r', ' ').Replace('\n', ' ');
    }
}
