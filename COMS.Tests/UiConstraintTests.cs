using System.ComponentModel.DataAnnotations;
using COMS_MVC.Helpers;
using COMS_MVC.Models;
using Xunit;

namespace COMS.Tests;

public sealed class UiConstraintTests
{
    [Theory]
    [InlineData("Critical", "danger", "st-critical")]
    [InlineData("High", "warning", "st-high")]
    [InlineData("Medium", "info", "st-medium")]
    [InlineData("Low", "success", "st-low-severity")]
    [InlineData(null, "secondary", "st-low")]
    [InlineData("Unknown", "secondary", "st-low")]
    public void Severity_UsesCanonicalColors(string? severity, string badge, string statusBadge)
    {
        Assert.Equal(badge, SeverityStyles.BadgeClass(severity));
        Assert.Equal(statusBadge, SeverityStyles.StatusBadgeClass(severity));
    }

    [Fact]
    public void ProductLengthLimits_MatchRequirements()
    {
        Assert.Equal(15, InputLimits.Username);
        Assert.Equal(40, InputLimits.FullName);
        Assert.Equal(25, InputLimits.CanalName);
    }

    [Fact]
    public void ProfileLocation_RejectsUnsupportedCharacters()
    {
        var model = new ProfileViewModel
        {
            FullName = "Maria Santos",
            Email = "maria@example.com",
            Barangay = "Centro",
            City = "Cebu<>City"
        };

        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(
            model, new ValidationContext(model), results, validateAllProperties: true));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ProfileViewModel.City)));
    }

    [Fact]
    public void ProfileLocation_AllowsOrdinaryValuesAndEmptyOptionalFields()
    {
        var model = new ProfileViewModel
        {
            FullName = "Maria Santos",
            Email = "maria@example.com",
            Barangay = "Barangay 1",
            City = "Cebu City",
            Address = "12 Osmeña Blvd."
        };

        var results = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(
            model, new ValidationContext(model), results, validateAllProperties: true));

        model.Barangay = null;
        model.City = null;
        model.Address = null;
        results.Clear();
        Assert.True(Validator.TryValidateObject(
            model, new ValidationContext(model), results, validateAllProperties: true));
    }
}
