using System.ComponentModel.DataAnnotations;

namespace COMS_MVC.Models
{
    public class LoginViewModel
    {
        [Required(ErrorMessage = "Username or email is required")]
        [Display(Name = "Username / Email")]
        public string Username { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }

    /// <summary>
    /// Shared character policy for free-text person/place/address fields.
    /// IMPORTANT: these patterns are emitted verbatim into data-val-regex
    /// attributes and executed by jQuery Validation as
    /// new RegExp(pattern) WITHOUT the unicode flag — so \p{L} and similar
    /// Unicode property escapes MUST NOT be used here (in JS they degrade to
    /// a literal "p" and reject nearly every real name). Only constructs with
    /// identical meaning in .NET and JavaScript are allowed: explicit \uXXXX
    /// ranges, lookaheads, and ASCII classes. Server and client therefore
    /// enforce byte-for-byte the same rule.
    /// </summary>
    internal static class RegistrationPatterns
    {
        // Invisible formatting / control characters, rejected everywhere.
        private const string BannedControls =
            @"\u0000-\u001F\u007F-\u009F\u200B-\u200F\u202A-\u202E\uFEFF";

        // Person, barangay, and city names: anything except controls,
        // invisible formatting, and angle brackets (defense in depth for any
        // unencoded sink), with at least one non-whitespace character so
        // whitespace-only values fail even after trimming.
        public const string NameOrPlace = @"^(?=.*\S)[^" + BannedControls + @"<>]*$";

        // Residential addresses: same as names, plus "@" is banned — "@"
        // never appears in a genuine street address, which keeps email
        // addresses out without affecting ordinary punctuation.
        public const string Address = @"^(?=.*\S)[^" + BannedControls + @"<>@]*$";
    }

    public class RegisterViewModel
    {
        [Required(ErrorMessage = "Full name is required")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Full name must be 2-100 characters")]
        [RegularExpression(RegistrationPatterns.NameOrPlace,
            ErrorMessage = "Full name contains unsupported characters.")]
        [Display(Name = "Full Name")]
        public string FullName { get; set; }

        [Required(ErrorMessage = "Username is required")]
        [StringLength(30, MinimumLength = 3, ErrorMessage = "Username must be 3-30 characters")]
        // Display-name policy, kept in sync with Identity
        // AllowedUserNameCharacters in Program.cs: letters, digits, spaces,
        // and -._@+. Leading/trailing spaces are trimmed before validation.
        // The UserValidator probe in AccountController.Register is
        // authoritative; this attribute gives immediate field-level feedback
        // on both client and server.
        [RegularExpression(@"^[a-zA-Z0-9\-._@+ ]+$",
            ErrorMessage = "Username may only contain letters, digits, spaces, and - . _ @ +.")]
        [Display(Name = "Username")]
        public string UserName { get; set; }

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [RegularExpression(@"^\S+$", ErrorMessage = "Invalid email format")]
        [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters")]
        [Display(Name = "Email")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Phone number is required")]
        [StringLength(30, ErrorMessage = "Phone number cannot exceed 30 characters")]
        // Philippine formats after separator normalization (see
        // AccountController.NormalizePhone): 09XXXXXXXXX or +639XXXXXXXXX.
        // [0-9] (not \d) so .NET and JavaScript accept exactly ASCII digits.
        [RegularExpression(@"^(\+63[0-9]{10}|09[0-9]{9})$",
            ErrorMessage = "Enter a valid Philippine mobile number (09XXXXXXXXX or +639XXXXXXXXX).")]
        [Display(Name = "Phone Number")]
        public string PhoneNumber { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [StringLength(100, ErrorMessage = "Password must be at least 6 characters", MinimumLength = 6)]
        [RegularExpression(@"^(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z0-9]).+$",
            ErrorMessage = "Password must contain at least 1 uppercase letter, 1 number, and 1 special character (e.g. !@#$%).")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }

        [Required(ErrorMessage = "Please confirm your password")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm Password")]
        [Compare("Password", ErrorMessage = "Passwords do not match")]
        public string ConfirmPassword { get; set; }

        [StringLength(100, ErrorMessage = "Barangay cannot exceed 100 characters")]
        [RegularExpression(RegistrationPatterns.NameOrPlace,
            ErrorMessage = "Barangay contains unsupported characters.")]
        [Display(Name = "Barangay")]
        public string? Barangay { get; set; }

        [StringLength(100, ErrorMessage = "City cannot exceed 100 characters")]
        [RegularExpression(RegistrationPatterns.NameOrPlace,
            ErrorMessage = "City / Municipality contains unsupported characters.")]
        [Display(Name = "City / Municipality")]
        public string? City { get; set; }

        [StringLength(250, ErrorMessage = "Address cannot exceed 250 characters")]
        [RegularExpression(RegistrationPatterns.Address,
            ErrorMessage = "Enter a valid residential address (email addresses are not accepted here).")]
        [Display(Name = "Address")]
        public string? Address { get; set; }

        [Display(Name = "Role")]
        public string Role { get; set; } = "Resident";
    }

    public class ForgotPasswordViewModel
    {
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;
    }

    public class AccountResetPasswordViewModel
    {
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Reset token is missing. Please use the link from your email.")]
        public string Token { get; set; } = string.Empty;

        [Required(ErrorMessage = "New password is required")]
        [StringLength(100, ErrorMessage = "Password must be at least 6 characters", MinimumLength = 6)]
        [RegularExpression(@"^(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z0-9]).+$",
            ErrorMessage = "Password must contain at least 1 uppercase letter, 1 number, and 1 special character (e.g. !@#$%).")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare("NewPassword", ErrorMessage = "Passwords do not match")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public class ProfileViewModel
    {
        [Display(Name = "Username")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Full name is required")]
        [StringLength(100)]
        [Display(Name = "Full Name")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        public string Email { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Invalid phone number format")]
        [Display(Name = "Phone Number")]
        public string? PhoneNumber { get; set; }

        public string? Barangay { get; set; }

        [Display(Name = "City / Municipality")]
        public string? City { get; set; }

        public string? Address { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Current password (only if changing password)")]
        public string? CurrentPassword { get; set; }

        [StringLength(100, ErrorMessage = "Password must be at least 6 characters", MinimumLength = 6)]
        [DataType(DataType.Password)]
        [Display(Name = "New password (optional)")]
        public string? NewPassword { get; set; }
    }

    public class DeleteAccountViewModel
    {
        public string UserName { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your password to confirm deletion.")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm with password")]
        public string Password { get; set; } = string.Empty;
    }

    public class VerifyOtpViewModel
    {
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Verification code is required")]
        [StringLength(6, MinimumLength = 6, ErrorMessage = "Enter the 6-digit code.")]
        [RegularExpression(@"^\d{6}$", ErrorMessage = "Enter the 6-digit code.")]
        [Display(Name = "Verification code")]
        public string Code { get; set; } = string.Empty;
    }
}
