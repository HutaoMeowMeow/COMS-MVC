using COMS_MVC.Helpers;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Relative date display for community reports (and every view sharing
/// PhTime.ToRelative): exact scale, future clamping, and UTC handling.
/// </summary>
public sealed class PhTimeTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewReport_ShowsJustNow()
    {
        Assert.Equal("Just now", PhTime.ToRelative(Now, Now));
        Assert.Equal("Just now", PhTime.ToRelative(Now.AddSeconds(-30), Now));
        Assert.Equal("Just now", PhTime.ToRelative(Now.AddSeconds(-59), Now));
    }

    [Theory]
    [InlineData(1, "1 minute ago")]
    [InlineData(2, "2 minutes ago")]
    [InlineData(30, "30 minutes ago")]
    [InlineData(59, "59 minutes ago")]
    public void Minutes_Scale(int minutes, string expected)
    {
        Assert.Equal(expected, PhTime.ToRelative(Now.AddMinutes(-minutes), Now));
    }

    [Theory]
    [InlineData(1, "1 hour ago")]
    [InlineData(2, "2 hours ago")]
    [InlineData(23, "23 hours ago")]
    public void Hours_Scale(int hours, string expected)
    {
        Assert.Equal(expected, PhTime.ToRelative(Now.AddHours(-hours), Now));
    }

    [Theory]
    [InlineData(1, "1 day ago")]
    [InlineData(2, "2 days ago")]
    [InlineData(6, "6 days ago")]
    public void Days_Scale(int days, string expected)
    {
        Assert.Equal(expected, PhTime.ToRelative(Now.AddDays(-days), Now));
    }

    [Fact]
    public void SevenDaysOrMore_ShowsPhDateOnly()
    {
        // Oct 13 UTC = Oct 13 PH (12:00 UTC + 8h, same date).
        Assert.Equal("Oct 06, 2026", PhTime.ToRelative(Now.AddDays(-7), Now));
        Assert.Equal("Sep 13, 2026", PhTime.ToRelative(Now.AddDays(-30), Now));
    }

    [Fact]
    public void FutureTimestamp_NeverShowsNegative()
    {
        Assert.Equal("Just now", PhTime.ToRelative(Now.AddMinutes(5), Now));
        Assert.Equal("Just now", PhTime.ToRelative(Now.AddDays(2), Now));
    }

    [Fact]
    public void UnspecifiedKind_TreatedAsUtc()
    {
        // Npgsql returns timestamps with Kind=Unspecified; they must compare
        // as UTC, not server-local time.
        var stored = DateTime.SpecifyKind(Now.AddMinutes(-5), DateTimeKind.Unspecified);
        Assert.Equal("5 minutes ago", PhTime.ToRelative(stored, Now));
    }

    [Fact]
    public void StoredReportTimestamp_RendersCorrectly()
    {
        // Simulates a report row read back from PostgreSQL (Unspecified kind).
        var createdAt = DateTime.SpecifyKind(Now.AddSeconds(-10), DateTimeKind.Unspecified);
        Assert.Equal("Just now", PhTime.ToRelative(createdAt, Now));
    }

    [Fact]
    public void FilterDateAndTime_ConvertsPhilippineWallClockToUtc()
    {
        Assert.True(PhTime.TryParsePhDateTime("2026-10-26", "14:30", false, out var utc));
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 10, 26, 6, 30, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void FilterDateOnly_UsesStartOfDay()
    {
        Assert.True(PhTime.TryParsePhDateTime("2026-10-26", null, false, out var utc));
        Assert.Equal(new DateTime(2026, 10, 25, 16, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void FilterEndDateOnly_IsInclusiveEndOfDay()
    {
        Assert.True(PhTime.TryParsePhDateTime("2026-10-26", "", true, out var utc));
        Assert.Equal(new DateTime(2026, 10, 26, 15, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999), utc);
    }

    [Theory]
    [InlineData("2026-13-01", null)]
    [InlineData("not-a-date", null)]
    [InlineData("2026-02-31", null)]
    [InlineData("2026-10-26", "25:00")]
    [InlineData("2026-10-26", "24:00")]
    [InlineData("2026-10-26", "1.5")]
    [InlineData("2026-10-26", "not-a-time")]
    public void FilterInvalidValues_AreRejected(string date, string? time)
    {
        Assert.False(PhTime.TryParsePhDateTime(date, time, false, out _));
    }
}
