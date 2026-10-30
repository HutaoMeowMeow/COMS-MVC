using COMS_MVC.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for the TicketModerationService (fix #5):
/// verifies category-specific validation, Other concern requirement,
/// spam filtering, and COMS-relevance enforcement.
/// </summary>
public sealed class TicketModerationTests
{
    private static TicketModerationService MakeService()
    {
        var textSvc = new TextModerationService(Options.Create(new TextModerationOptions()));
        return new TicketModerationService(textSvc, NullLogger<TicketModerationService>.Instance);
    }

    [Fact]
    public async Task ValidTicket_SubmittedForAccountCategory_IsAccepted()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Account",
            "Cannot log into my account",
            "I'm trying to sign in but it says my password is incorrect. I've verified my email is confirmed. I need to view my canal water level alerts.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.Equal("Accept", result.Status);
        Assert.False(result.IsRejected);
        Assert.False(result.IsUncertain);
    }

    [Fact]
    public async Task OtherCategory_WithoutOtherConcern_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Other",
            "Miscellaneous issue",
            "I have a question about something.",
            otherConcern: null,
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
        Assert.Contains(result.Errors, e => e.Contains("Other concern"));
    }

    [Fact]
    public async Task OtherCategory_WithOtherConcern_IsAccepted()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Other",
            "Question about payments",
            "I have a question about payments for the canal maintenance program.",
            otherConcern: "I want to know if I can pay my canal service fees online.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public async Task OtherConcern_ExceedingMaxLength_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Other",
            "Misc",
            "Some description about account access.",
            otherConcern: new string('x', 501),
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
        Assert.Contains(result.Errors, e => e.Contains("500 characters"));
    }

    [Fact]
    public async Task InvalidCategory_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "NonexistentCategory",
            "Test subject",
            "Test description",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
        Assert.Contains(result.Errors, e => e.Contains("valid category"));
    }

    [Fact]
    public async Task EmptySubject_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Account",
            "",
            "Description text",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
        Assert.Contains(result.Errors, e => e.Contains("Subject is required"));
    }

    [Fact]
    public async Task EmptyDescription_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Account",
            "Subject here",
            "",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
        Assert.Contains(result.Errors, e => e.Contains("Description is required"));
    }

    [Fact]
    public async Task SubjectExceedingMaxLength_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Account",
            new string('x', 151),
            "Description text",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
        Assert.Contains(result.Errors, e => e.Contains("150 characters"));
    }

    [Fact]
    public async Task DescriptionExceedingMaxLength_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Account",
            "Subject",
            new string('x', 2001),
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
        Assert.Contains(result.Errors, e => e.Contains("2000 characters"));
    }

    [Fact]
    public async Task SpamContent_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "General Inquiry",
            "asdfghjkl asdfghjkl",
            "qwertyuiop qwertyuiop zxcvbnm zxcvbnm",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
    }

    [Fact]
    public async Task RepeatedNumbers_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Technical Issue",
            "123123123123",
            "Blocked canal and need help.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
    }

    [Fact]
    public async Task NonComsRelatedContent_IsMarkedUncertain()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "General Inquiry",
            "Cooking recipe question",
            "How do I make the best pasta sauce? I need help with the recipe and ingredients.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.Equal("Uncertain", result.Status);
        Assert.Equal("Under Review", result.SuggestedStatus);
    }

    [Fact]
    public async Task NonComsContentInOtherCategory_IsMarkedUncertain()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Other",
            "Movie recommendation",
            "Can you suggest a good sci-fi movie to watch tonight?",
            otherConcern: "I want movie recommendations.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.Equal("Uncertain", result.Status);
    }

    [Fact]
    public async Task TechIssue_WithMeaningfulContent_IsAccepted()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Technical Issue",
            "Map not loading on mobile",
            "The canal map on the dashboard won't load when I open the app on my phone. I've tried clearing the cache and it still doesn't show any canals.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public async Task AllFiveCategories_AreRecognized()
    {
        var svc = MakeService();
        var categories = new[] { "Account", "Report", "Technical Issue", "General Inquiry", "Other" };

        foreach (var category in categories)
        {
            if (category == "Other")
            {
                var result = await svc.ModerateAsync(
                    category, "Other subject", "Description text", otherConcern: "Specific concern",
                    150, 2000);
                Assert.False(result.IsRejected, $"Category '{category}' should not be rejected for missing OtherConcern");
            }
            else
            {
                var result = await svc.ModerateAsync(
                    category, "Subject", "Description about canal drainage water flooding.",
                    subjectMaxLength: 150, descriptionMaxLength: 2000);
                Assert.False(result.IsRejected, $"Category '{category}' should be recognized");
            }
        }
    }

    [Fact]
    public async Task ProfanityWithoutSanitationContext_IsRejected()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "General Inquiry",
            "Hello there",
            "This is a fucking terrible experience I am having.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.True(result.IsRejected);
    }

    [Fact]
    public async Task CaseInsensitiveCategoryMatching()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "account",
            "Login issue",
            "I cannot log into my account to view canal water levels. The login page shows an error.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        // "account" should be recognized as "Account" (case-insensitive)
        Assert.DoesNotContain(result.Errors, e => e.Contains("valid category"));
        Assert.False(result.IsRejected);
    }

    [Fact]
    public async Task OtherCategory_EmptyDescription_DoesNotRequireDescription()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Other",
            "Miscellaneous issue",
            "",
            otherConcern: "I need help with my canal service billing question.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        // Description is NOT required for Other category — only OtherConcern.
        Assert.False(result.IsRejected);
        Assert.DoesNotContain(result.Errors, e => e.Contains("Description is required"));
    }

    [Fact]
    public async Task NonOtherCategory_EmptyOtherConcern_IsValid()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Account",
            "Login issue",
            "I cannot log into my account to view canal water levels. The login page shows an error.",
            otherConcern: null,
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        // OtherConcern is not required for Account category.
        Assert.False(result.IsRejected);
    }

    [Fact]
    public async Task OtherCategory_OtherConcernUsedAsDescriptionForModeration()
    {
        var svc = MakeService();
        // Spam content only in OtherConcern, empty Description.
        var result = await svc.ModerateAsync(
            "Other",
            "asdfghjkl",
            "",
            otherConcern: "qwertyuiop qwertyuiop zxcvbnm zxcvbnm",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        // The OtherConcern is treated as the description and should be
        // moderated for spam just like a regular Description.
        Assert.True(result.IsRejected);
    }

    [Fact]
    public async Task OtherCategory_ComsRelevantOtherConcern_IsAccepted()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "Other",
            "Canal maintenance question",
            "",
            otherConcern: "I want to know when the next canal dredging schedule will be posted on the dashboard.",
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        Assert.Equal("Accept", result.Status);
    }

    [Fact]
    public async Task NonOtherCategory_OtherConcernNotValidated()
    {
        var svc = MakeService();
        var result = await svc.ModerateAsync(
            "General Inquiry",
            "Test subject",
            "Canal flooding report text here.",
            otherConcern: new string('x', 501),
            subjectMaxLength: 150,
            descriptionMaxLength: 2000);

        // OtherConcern is not validated for non-Other categories.
        Assert.False(result.IsRejected);
        Assert.DoesNotContain(result.Errors, e => e.Contains("500 characters"));
    }
}
