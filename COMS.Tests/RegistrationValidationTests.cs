using System.ComponentModel.DataAnnotations;
using COMS_MVC.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Problem 1+2: registration field rules enforced on the registration page.
/// Validates RegisterViewModel DataAnnotations directly (the same rules the
/// Register POST re-runs after normalization), plus consistency between the
/// username regex and the real ASP.NET Core Identity UserValidator.
/// </summary>
public sealed class RegistrationValidationTests
{
    private static RegisterViewModel Valid() => new()
    {
        FullName = "Maria Santos",
        UserName = "mariasantos",
        Email = "maria@example.com",
        PhoneNumber = "09123456789",
        Password = "Secret@123",
        ConfirmPassword = "Secret@123",
        Barangay = "Centro",
        City = "Cebu City",
        Address = "123 Mango St.",
        Role = "Resident"
    };

    private static List<ValidationResult> Validate(RegisterViewModel model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static void AssertFieldError(List<ValidationResult> results, string field)
    {
        Assert.Contains(results, r => r.MemberNames.Contains(field));
    }

    [Fact]
    public void ValidRegistration_PassesValidation()
    {
        Assert.Empty(Validate(Valid()));
    }

    [Fact]
    public void DisplayUsernames_WithSpaces_AreAccepted()
    {
        foreach (var ok in new[] { "MANOK", "Manok Manok", "GianLoza123", "Gian_Loza", "Gian-Loza" })
        {
            var m = Valid();
            m.UserName = ok;
            Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.UserName)));
        }
    }

    [Fact]
    public void Username_WithUnsupportedPunctuation_IsRejected()
    {
        foreach (var bad in new[] { "user!name", "user/name", "user,name", "user;name" })
        {
            var m = Valid();
            m.UserName = bad;
            AssertFieldError(Validate(m), nameof(m.UserName));
        }
    }

    [Fact]
    public void Username_AllowedIdentityCharacters_PassRegex()
    {
        // Letters, digits, spaces, and -._@+ per the configured policy.
        foreach (var ok in new[] { "user123", "user.name", "user_name", "user-name", "user@x", "user+x", "Manok Manok" })
        {
            var m = Valid();
            m.UserName = ok;
            Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.UserName)));
        }
    }

    [Fact]
    public void Username_TooShort_And_TooLong_AreRejected()
    {
        var short_ = Valid();
        short_.UserName = "ab";
        AssertFieldError(Validate(short_), nameof(short_.UserName));

        var long_ = Valid();
        long_.UserName = new string('a', 31);
        AssertFieldError(Validate(long_), nameof(long_.UserName));
    }

    [Fact]
    public async Task UsernameRegex_Matches_RealIdentityUserValidator()
    {
        // The regex must never accept what Identity itself would reject, and
        // must accept ordinary valid usernames — verified against the real
        // UserValidator with the production-like options (spaces allowed).
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<COMS_MVC.Data.ApplicationDbContext>()
            .UseSqlite(conn).Options;
        using var db = new COMS_MVC.Data.ApplicationDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var manager = TestUserManagers.CreateUserManager(db, TestUserManagers.ProgramLikeOptions());
        var validator = new UserValidator<ApplicationUser>();
        foreach (var name in new[] { "mariasantos", "Manok Manok", "user.name_1", "USER-2@x", "a b", "x!" })
        {
            var probe = new ApplicationUser { UserName = name };
            var identityResult = await validator.ValidateAsync(manager, probe);
            var m = Valid();
            m.UserName = name;
            var regexFails = Validate(m).Any(r => r.MemberNames.Contains(nameof(m.UserName)));
            if (identityResult.Succeeded)
            {
                Assert.False(regexFails, $"Regex rejects '{name}' but Identity accepts it.");
            }
            else
            {
                Assert.True(regexFails, $"Regex accepts '{name}' but Identity rejects it.");
            }
        }
    }

    [Fact]
    public void InvalidEmail_IsRejected()
    {
        // NOTE: "a@b" is accepted by the app's [EmailAddress] convention
        // (single-label domains are technically valid); it is not tested here.
        foreach (var bad in new[] { "not-an-email", "@x.com", "a b@c.com", "" })
        {
            var m = Valid();
            m.Email = bad;
            AssertFieldError(Validate(m), nameof(m.Email));
        }
    }

    [Fact]
    public void Email_TooLong_IsRejected()
    {
        var m = Valid();
        m.Email = new string('a', 260) + "@x.com";
        AssertFieldError(Validate(m), nameof(m.Email));
    }

    [Fact]
    public void InvalidPhoneNumbers_AreRejected()
    {
        foreach (var bad in new[] { "abc", "09123", "091234567890", "639123456789", "+640912345678", "09-123", "" })
        {
            var m = Valid();
            m.PhoneNumber = bad;
            AssertFieldError(Validate(m), nameof(m.PhoneNumber));
        }
    }

    [Fact]
    public void ValidPhilippinePhones_Pass()
    {
        foreach (var ok in new[] { "09123456789", "+639123456789" })
        {
            var m = Valid();
            m.PhoneNumber = ok;
            Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.PhoneNumber)));
        }
    }

    [Fact]
    public void MissingRequiredFields_AreRejected()
    {
        var m = new RegisterViewModel
        {
            Password = "Secret@123",
            ConfirmPassword = "Secret@123",
            Role = "Resident"
        };
        var results = Validate(m);
        AssertFieldError(results, nameof(m.FullName));
        AssertFieldError(results, nameof(m.UserName));
        AssertFieldError(results, nameof(m.Email));
        AssertFieldError(results, nameof(m.PhoneNumber));
    }

    [Fact]
    public void WhitespaceOnly_FullName_IsRejected()
    {
        var m = Valid();
        m.FullName = "   ";
        AssertFieldError(Validate(m), nameof(m.FullName));
    }

    [Fact]
    public void Overlong_Name_Address_Location_AreRejected()
    {
        var m = Valid();
        m.FullName = new string('a', 101);
        m.Address = new string('a', 251);
        m.Barangay = new string('a', 101);
        m.City = new string('a', 101);
        var results = Validate(m);
        AssertFieldError(results, nameof(m.FullName));
        AssertFieldError(results, nameof(m.Address));
        AssertFieldError(results, nameof(m.Barangay));
        AssertFieldError(results, nameof(m.City));
    }

    [Fact]
    public void LegitimateNames_WithHyphensApostrophesUnicode_Pass()
    {
        foreach (var ok in new[] { "María Santos-Dela Cruz", "O'Brien", "Anne Marie", "José Rizal" })
        {
            var m = Valid();
            m.FullName = ok;
            Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.FullName)));
        }
    }

    [Fact]
    public void TaskExamples_FullNames_AreAccepted()
    {
        foreach (var ok in new[]
        {
            "Gian Ethan Loza", "Gian Carlo Ethan", "Juan Dela Cruz",
            "Maria Santos-Reyes", "Anne O'Connor", "José María Santos",
            "Gian E. Loza"
        })
        {
            var m = Valid();
            m.FullName = ok;
            Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.FullName)));
        }
    }

    [Fact]
    public void TaskExamples_UrgentCases_Pass()
    {
        // Exact values reported as wrongly rejected: they must validate.
        var m = Valid();
        m.FullName = "Gian Carlo Ethan";
        m.Barangay = "Kalunasan";
        m.City = "Cebu City";
        Assert.Empty(Validate(m));
    }

    [Fact]
    public void Names_WithMultipleInnerSpaces_AreAccepted()
    {
        var m = Valid();
        m.FullName = "Gian  Carlo   Ethan";
        Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.FullName)));
    }

    [Fact]
    public void Address_EmailAddress_IsRejected_OrdinaryAddressesPass()
    {
        var m = Valid();
        m.Address = "hutao paitao@gmail.com";
        AssertFieldError(Validate(m), nameof(m.Address));

        foreach (var ok in new[]
        {
            "123 Mango Avenue", "Block 5, Lot 12, Phase 2",
            "Unit 4B, Building 3", "Sitio Upper Kalunasan, Cebu City"
        })
        {
            var good = Valid();
            good.Address = ok;
            Assert.DoesNotContain(Validate(good), r => r.MemberNames.Contains(nameof(good.Address)));
        }
    }

    [Fact]
    public void RegisterViewModel_RegexPatterns_ContainNoUnicodePropertyEscapes()
    {
        // Regression guard for the client/server split-brain bug: patterns
        // are emitted to jQuery Validation, which runs new RegExp(pattern)
        // WITHOUT the unicode flag — \p{...} would degrade to a literal
        // "p" in the browser and reject valid input client-side only.
        var props = typeof(RegisterViewModel).GetProperties();
        foreach (var prop in props)
        {
            foreach (RegularExpressionAttribute attr in
                prop.GetCustomAttributes(typeof(RegularExpressionAttribute), inherit: true))
            {
                Assert.DoesNotContain(@"\p{", attr.Pattern);
                Assert.DoesNotContain(@"\P{", attr.Pattern);
            }
        }
    }

    [Fact]
    public void TaskExamples_PlaceNames_AreAccepted()
    {
        var places = new[]
        {
            ("Kalunasan", "Cebu City"), ("Barangay San José", "Cebu City"),
            ("Poblacion 1", "Lapu-Lapu City"), ("Kalunasan", "Cebu City")
        };
        foreach (var (barangay, city) in places)
        {
            var m = Valid();
            m.Barangay = barangay;
            m.City = city;
            var results = Validate(m);
            Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(m.Barangay)));
            Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(m.City)));
        }
    }

    [Fact]
    public void TaskExamples_Addresses_AreAccepted()
    {
        foreach (var ok in new[]
        {
            "123 Mango Avenue", "Block 5, Lot 12, Phase 2",
            "Unit 4B, Building 3", "Sitio Upper Kalunasan, Cebu City"
        })
        {
            var m = Valid();
            m.Address = ok;
            Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.Address)));
        }
    }

    [Fact]
    public void TaskExamples_PhoneNumbers_AreAccepted()
    {
        foreach (var ok in new[] { "09297722832", "+639297722832", "09123456789", "+639123456789" })
        {
            var m = Valid();
            m.PhoneNumber = ok;
            Assert.DoesNotContain(Validate(m), r => r.MemberNames.Contains(nameof(m.PhoneNumber)));
        }
    }

    [Fact]
    public void ControlCharacters_AreRejected()
    {
        var m = Valid();
        m.FullName = "John\u0007Doe";
        AssertFieldError(Validate(m), nameof(m.FullName));

        m = Valid();
        m.UserName = "ab\u200Bc";
        AssertFieldError(Validate(m), nameof(m.UserName));

        m = Valid();
        m.Address = "Street\u0000Name";
        AssertFieldError(Validate(m), nameof(m.Address));

        m = Valid();
        m.City = "Cebu\u007FCity";
        AssertFieldError(Validate(m), nameof(m.City));
    }

    [Fact]
    public void WeakPasswords_AreRejected()
    {
        // Too short / missing uppercase / missing digit / missing special.
        foreach (var bad in new[] { "Ab1!", "secret@123", "Secret@abc", "Secret1234" })
        {
            var m = Valid();
            m.Password = bad;
            m.ConfirmPassword = bad;
            AssertFieldError(Validate(m), nameof(m.Password));
        }
    }

    [Fact]
    public void MismatchedConfirmation_IsRejected()
    {
        var m = Valid();
        m.ConfirmPassword = "Different@123";
        AssertFieldError(Validate(m), nameof(m.ConfirmPassword));
    }
}
