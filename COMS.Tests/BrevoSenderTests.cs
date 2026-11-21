using System.Net;
using System.Text;
using System.Text.Json;
using COMS_MVC.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// The Brevo sender must never be empty, a placeholder or the old Resend
/// sandbox address. All HTTP goes to a fake handler; nothing leaves the process.
/// </summary>
public sealed class BrevoSenderTests
{
    private const string Key = "xkeysib-test-not-a-real-key";
    private const string Sender = "alerts@coms-ph.org";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        public FakeHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private static (BrevoEmailService Service, FakeHandler Handler) Build(
        string senderEmail, HttpStatusCode status = HttpStatusCode.Created, string body = """{"messageId":"<abc@smtp-relay.brevo.com>"}""")
    {
        var handler = new FakeHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.brevo.com/v3/") };
        var options = Options.Create(new BrevoOptions { ApiKey = Key, SenderEmail = senderEmail, SenderName = "COMS Alerts" });
        return (new BrevoEmailService(http, options, NullLogger<BrevoEmailService>.Instance), handler);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("COMS <alerts@coms-ph.org>")]
    [InlineData(" alerts@coms-ph.org")]
    [InlineData("onboarding@resend.dev")]
    [InlineData("ONBOARDING@RESEND.DEV")]
    [InlineData("x@mail.resend.dev")]
    [InlineData("no-reply@yourdomain.com")]
    [InlineData("a@example.com")]
    [InlineData("a@example.org")]
    [InlineData("a@example.net")]
    [InlineData("a@test.com")]
    [InlineData("a@localhost")]
    public void SenderProblem_RejectsMissingInvalidAndPlaceholder(string? email)
    {
        var problem = BrevoOptions.SenderProblem(email);
        Assert.NotNull(problem);
        if (email != null && email.Contains('@')) Assert.DoesNotContain(email.Trim(), problem); // raw value never echoed
    }

    [Theory]
    [InlineData("onboarding@resend.dev")]
    [InlineData("ONBOARDING@RESEND.DEV")]
    [InlineData("x@mail.resend.dev")]
    public void SenderProblem_RejectsResendByDomain(string email)
    {
        Assert.Contains("placeholder", BrevoOptions.SenderProblem(email));
    }

    [Theory]
    [InlineData(Sender)]
    [InlineData("no-reply@coms.gov.ph")]
    [InlineData("someone@gmail.com")]
    [InlineData("a@notexample.com")]
    [InlineData("Alerts+ops@Mail.LGU.gov.ph")]
    [InlineData("no-reply@mail.lgu.gov.ph")]
    public void SenderProblem_AcceptsRealAddresses(string email)
    {
        Assert.Null(BrevoOptions.SenderProblem(email));
    }

    [Theory]
    [InlineData("onboarding@resend.dev")]
    [InlineData("no-reply@yourdomain.com")]
    [InlineData("not-an-email")]
    public async Task InvalidSender_FailsWithoutHttpCall(string sender)
    {
        var (svc, handler) = Build(sender);

        await Assert.ThrowsAsync<EmailServiceException>(() => svc.SendOtpEmailAsync("resident@coms-ph.org", "123456"));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ValidSender_PostsConfiguredSenderAndRecipient_SucceedsOn201()
    {
        var (svc, handler) = Build(Sender);

        await svc.SendOtpEmailAsync("resident@coms-ph.org", "123456"); // no throw == success

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.ToString());
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues("api-key")));

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(Sender, root.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("COMS Alerts", root.GetProperty("sender").GetProperty("name").GetString());
        var to = Assert.Single(root.GetProperty("to").EnumerateArray());
        Assert.Equal("resident@coms-ph.org", to.GetProperty("email").GetString());
        Assert.DoesNotContain("resend.dev", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Brevo400SenderRejection_ReturnsFailure()
    {
        var (svc, handler) = Build(Sender, HttpStatusCode.BadRequest,
            """{"code":"invalid_parameter","message":"Sender is not valid. Please check the sender email."}""");

        await Assert.ThrowsAsync<EmailServiceException>(() => svc.SendEmailAsync("resident@coms-ph.org", "s", "<p>x</p>"));
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public async Task Success_WithMissingOrOddBody_StillSucceeds(string body)
    {
        var (svc, handler) = Build(Sender, HttpStatusCode.Created, body);

        await svc.SendEmailAsync("resident@coms-ph.org", "s", "<p>x</p>"); // any 2xx == success, as before
        Assert.Single(handler.Calls);
    }
}
