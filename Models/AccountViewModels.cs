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

    public class RegisterViewModel
    {
        [Required(ErrorMessage = "Full name is required")]
        [Display(Name = "Full Name")]
        public string FullName { get; set; }

        [Required(ErrorMessage = "Username is required")]
        [Display(Name = "Username")]
        public string UserName { get; set; }

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [Display(Name = "Email")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Phone number is required")]
        [Phone(ErrorMessage = "Invalid phone number format")]
        [Display(Name = "Phone Number")]
        public string PhoneNumber { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [StringLength(100, ErrorMessage = "Password must be at least 6 characters", MinimumLength = 6)]
        [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z0-9]).+$",
            ErrorMessage = "Password must contain at least 1 uppercase letter, 1 lowercase letter, 1 number, and 1 special character (e.g. !@#$%).")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }

        [Required(ErrorMessage = "Please confirm your password")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm Password")]
        [Compare("Password", ErrorMessage = "Passwords do not match")]
        public string ConfirmPassword { get; set; }

        [Display(Name = "Barangay")]
        public string? Barangay { get; set; }

        [Display(Name = "City / Municipality")]
        public string? City { get; set; }

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
        [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z0-9]).+$",
            ErrorMessage = "Password must contain at least 1 uppercase letter, 1 lowercase letter, 1 number, and 1 special character (e.g. !@#$%).")]
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
