using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for the OTP policy (risk A): cooldowns, expiry, attempt
/// limits, single-use/replay prevention, and safe email-failure rollback.
/// </summary>
public sealed class OtpServiceTests
{
    private sealed class FakeEmail : IEmailService
    {
        public int Sends;
        public bool ThrowNext;

        public Task SendEmailAsync(string to, string subject, string html, CancellationToken ct = default)
        {
            Sends++;
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new EmailServiceException("provider down");
            }
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(string to, string link, CancellationToken ct = default)
            => SendEmailAsync(to, "reset", link, ct);

        public Task SendPasswordResetCodeAsync(string to, string code, CancellationToken ct = default)
            => SendEmailAsync(to, "code", code, ct);

        public Task SendOtpEmailAsync(string to, string code, CancellationToken ct = default)
            => SendEmailAsync(to, "otp", code, ct);
    }

    private static PendingRegistration Pending() => new()
    {
        Email = "user@example.com",
        NormalizedEmail = "USER@EXAMPLE.COM",
        UserName = "user1",
        NormalizedUserName = "USER1",
        FullName = "Test User",
        Role = "Resident",
        PasswordHash = "hash",
        CreatedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
    };

    private static OtpService Service(FakeEmail email)
        => new(email, NullLogger<OtpService>.Instance);

    /// <summary>Recover the plain code the service hashed (test-only reversal).</summary>
    private static string BruteForceCode(PendingRegistration pending)
    {
        for (var n = 0; n < 1_000_000; n++)
        {
            var code = n.ToString("D6");
            if (code == "000000")
            {
                continue;
            }
            if (OtpService.Hash(code, pending.Email) == pending.OtpHash)
            {
                return code;
            }
        }
        throw new Xunit.Sdk.XunitException("Could not recover OTP code.");
    }

    [Fact]
    public async Task Issue_SetsHashExpiry_AndSendsOnce()
    {
        var email = new FakeEmail();
        var pending = Pending();
        var result = await Service(email).IssueAsync(pending);

        Assert.True(result.Succeeded);
        Assert.NotNull(pending.OtpHash);
        Assert.NotNull(pending.OtpExpiresAtUtc);
        Assert.Equal(1, email.Sends);
        Assert.Equal(1, pending.RequestCount);
    }

    [Fact]
    public async Task Issue_WithinCooldown_IsRejected_WithoutSending()
    {
        var email = new FakeEmail();
        var pending = Pending();
        var svc = Service(email);
        Assert.True((await svc.IssueAsync(pending)).Succeeded);

        var second = await svc.IssueAsync(pending);
        Assert.False(second.Succeeded);
        Assert.Contains("wait", second.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, email.Sends);
    }

    [Fact]
    public async Task Issue_BeyondHourlyLimit_IsRejected()
    {
        var email = new FakeEmail();
        var pending = Pending();
        // Simulate 5 sends spread outside the cooldown.
        for (var i = 0; i < OtpService.MaxSendsPerWindow; i++)
        {
            pending.LastSentAtUtc = DateTime.UtcNow.AddMinutes(-5);
            var r = await Service(email).IssueAsync(pending);
            Assert.True(r.Succeeded);
        }
        pending.LastSentAtUtc = DateTime.UtcNow.AddMinutes(-5);
        var limited = await Service(email).IssueAsync(pending);
        Assert.False(limited.Succeeded);
        Assert.Equal(OtpService.MaxSendsPerWindow, email.Sends);
    }

    [Fact]
    public async Task Verify_CorrectCode_Succeeds_AndCannotBeReplayed()
    {
        var email = new FakeEmail();
        var pending = Pending();
        var svc = Service(email);
        Assert.True((await svc.IssueAsync(pending)).Succeeded);
        var code = BruteForceCode(pending);

        Assert.True((await svc.VerifyAsync(pending, code)).Succeeded);
        // Single-use: the hash is cleared, so replay fails.
        var replay = await svc.VerifyAsync(pending, code);
        Assert.False(replay.Succeeded);
    }

    [Fact]
    public async Task Verify_WrongCodes_CountAttempts_ThenLock()
    {
        var email = new FakeEmail();
        var pending = Pending();
        var svc = Service(email);
        Assert.True((await svc.IssueAsync(pending)).Succeeded);

        for (var i = 0; i < OtpService.MaxVerifyAttempts; i++)
        {
            var r = await svc.VerifyAsync(pending, "999999");
            Assert.False(r.Succeeded);
        }
        Assert.Equal(OtpService.MaxVerifyAttempts, pending.FailedAttempts);
        var locked = await svc.VerifyAsync(pending, "999999");
        Assert.False(locked.Succeeded);
        Assert.Contains("Too many", locked.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verify_ExpiredCode_IsRejected()
    {
        var email = new FakeEmail();
        var pending = Pending();
        var svc = Service(email);
        Assert.True((await svc.IssueAsync(pending)).Succeeded);
        pending.OtpExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);

        var r = await svc.VerifyAsync(pending, BruteForceCode(pending));
        Assert.False(r.Succeeded);
        Assert.Contains("expired", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Issue_EmailFailure_RollsBack_NoPhantomCode_NoBudgetConsumed()
    {
        var email = new FakeEmail { ThrowNext = true };
        var pending = Pending();
        var before = DateTime.UtcNow.AddMinutes(-10);
        pending.LastSentAtUtc = before;

        var r = await Service(email).IssueAsync(pending);
        Assert.False(r.Succeeded);
        Assert.Null(pending.OtpHash);
        Assert.Null(pending.OtpExpiresAtUtc);
        Assert.Equal(0, pending.RequestCount);
        Assert.Equal(before, pending.LastSentAtUtc);
    }

    [Fact]
    public void GenerateCode_IsSixDigits_NeverAllZero()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = OtpService.GenerateCode();
            Assert.Matches(@"^\d{6}$", code);
            Assert.NotEqual("000000", code);
        }
    }
}
