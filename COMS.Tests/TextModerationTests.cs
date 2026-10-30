using COMS_MVC.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Unit tests for the server-side report text moderation (fix #3).
/// No database is required; the service is deterministic and dependency-free.
/// </summary>
public sealed class TextModerationTests
{
    private static TextModerationService Service(TextModerationOptions? options = null) =>
        new(Options.Create(options ?? new TextModerationOptions()));

    private static TextModerationResult Analyze(TextModerationService svc, string? title, string? description) =>
        svc.AnalyzeText(title, description);

    [Fact]
    public void MeaningfulBlockedCanalReport_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc,
            "Drainage near the road keeps getting blocked",
            "Water accumulates every time it rains and the canal is clogged with garbage.");
        Assert.Equal("Accept", result.Status);
        Assert.Equal("Pending", result.SuggestedStatus);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void FloodingReport_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc,
            "Flood water covering the street",
            "Floodwater reached knee level during last night's heavy rain near the barangay hall.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void CebuanoReport_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc,
            "Bumbong sa kanal sa baybayon",
            "Umaabot na ang tubig sa daan kapag umuulan.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void FilipinoReport_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc,
            "Bumbog na kanal",
            "Nabansagan ang kanal ng basura at luma ang estero.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void SanitationReportWithSensitiveTerm_IsNotAutoRejected()
    {
        var svc = Service();
        var result = Analyze(svc,
            "Human waste overflowing into the canal",
            "Septic waste is flowing into the drainage canal during rain and smells bad.");
        // Sanitation vocabulary is legitimate report language; no profanity/sexual
        // blocklist term is present, so it must be accepted, not rejected.
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void TitleOnlyRepeatedNumbers_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "123123123123", "A canal.");
        Assert.Equal("Reject", result.Status);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void RepeatedNumericString_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "5555555555", "Blocked canal.");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void KeyboardMashing_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "asdfghjkl", "x");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void RandomSymbols_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "@#$%@#$%@#$%", "x");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void RepetitionSpam_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "FREE!!!!! MONEY!!!!! FREE!!!!!", "Canal.");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void ClearlyUnrelatedSpam_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "Buy cheap meds online now!!!!!", "Canal blockage.");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void ExplicitSexualSpam_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "porn video near the canal", "Explicit content unrelated to drainage.");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void TargetedProfanity_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "this damn canal is shit", "Blocked.");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void EmptyTitle_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "", "A blocked canal.");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void WhitespaceOnlyTitle_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "   ", "A blocked canal.");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void TitleContainingNumbers_IsNotAutomaticallyRejected()
    {
        var svc = Service();
        var result = Analyze(svc,
            "Canal 3 near Sitio Mahaba blocked by debris",
            "The third canal segment keeps overflowing during heavy rain.");
        // Numbers alone must not trigger rejection.
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ShortLegitimateTitles_AreAccepted()
    {
        var svc = Service();
        foreach (var title in new[] { "Blocked canal", "Flooding", "Damaged drain", "Trash in waterway" })
        {
            var result = Analyze(svc, title, "Water is not draining properly.");
            Assert.True(result.Status == "Accept", title);
        }
    }

    [Fact]
    public void AmbiguousContent_IsFlaggedForReview_NotAutoRejected()
    {
        var svc = Service();
        // A single mild signal (all-caps title) pushes to Uncertain /
        // Under Review rather than outright rejection when no stronger signal exists.
        var result = Analyze(svc, "EMERGENCY", "Water somewhere near the market.");
        Assert.Equal("Uncertain", result.Status);
        Assert.Equal("Under Review", result.SuggestedStatus);
    }

    // ---- New: description meaningfulness (fix #3 extended) ----

    [Fact]
    public void GibberishDescription_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "Blocked canal", "WJENFIWJMDEQUGHWRFWEF");
        Assert.Equal("Reject", result.Status);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void KeyboardRowDescription_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "Report", "asdfghjkl qwertyuiop zxcvbnm");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void RepeatedWordDescription_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "Issue", "Hello hello hello hello hello");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void ValidEnglishBlockedCanal_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Blocked canal", "Garbage is blocking the canal near our street.");
        Assert.Equal("Accept", result.Status);
        Assert.Equal("Pending", result.SuggestedStatus);
    }

    [Fact]
    public void ValidEnglishFlooding_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Flooding", "Water is overflowing onto the road during heavy rain.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ValidEnglishDamagedCanal_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Damaged canal", "The canal is blocked by plastic waste.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ValidShortEnglish_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Blocked", "Canal blocked.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ValidCebuano_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Baha", "Baha na ang kanal sa among lugar.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ValidFilipino_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Baha", "Nabahahan ang kalye dahil sa maulang ulan.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void DescriptionWithStreetNumber_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Blocked canal", "Canal at 123 Main Street is blocked by garbage.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void MinorSpellingErrors_AreStillAccepted()
    {
        var svc = Service();
        // One typo ("garbaj") must not reject an otherwise meaningful report
        // that still contains recognizable COMS keywords.
        var result = Analyze(svc, "Blocked canal", "The canal is blocked by garbaj near the street.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void UnseenRandomLetterToken_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "Canal issue", "ZXQKJHFDSLQW");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void RepeatedLetterPattern_IsRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "Issue", "ABCABCABC");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void ValidVagueFloodDescription_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Flooding", "The water keeps rising and will soon enter our house.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ValidMixedLanguageDescription_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Blocked canal", "Barado ang canal near our street, full of basura.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ShortDescriptionWithTypo_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Blocked canal", "Blckd near 5th Street.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ValidDescriptionRepeatingCommonWords_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Blocked canal",
            "The canal is blocked. The garbage is near the road. The water is high.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void ValidCebuanoRepeatingTopicWord_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Baha",
            "Baha sa among lugar, baha sa among kalsada, baha sa among balay.");
        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public void PartialKeyboardRows_AreRejected()
    {
        var svc = Service();
        var result = Analyze(svc, "Issue", "qwerty asdf zxcv");
        Assert.Equal("Reject", result.Status);
    }

    [Fact]
    public void LegitimateWordContainingKeyLetters_IsAccepted()
    {
        var svc = Service();
        var result = Analyze(svc, "Flooding", "The property near the canal is flooded.");
        Assert.Equal("Accept", result.Status);
    }
}
