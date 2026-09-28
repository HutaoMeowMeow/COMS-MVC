namespace COMS_MVC.Services
{
    /// <summary>
    /// Strongly-typed SMTP options, bound from appsettings.json -> "EmailSettings".
    /// Real secrets come from EmailSettings__&lt;Key&gt; environment variables,
    /// which overlay the file values at runtime. Never commit real credentials.
    /// </summary>
    public class EmailSettings
    {
        public string SmtpServer { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 587;
        public string SenderName { get; set; } = "COMS";
        public string SenderEmail { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool EnableSsl { get; set; } = true;
    }
}
