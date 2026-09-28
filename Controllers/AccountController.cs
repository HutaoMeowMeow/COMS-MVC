using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;

namespace COMS_MVC.Controllers
{
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole<int>> _roleManager;
        private readonly IEmailService _emailService;
        private readonly ILogger<AccountController> _logger;

        public AccountController(UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole<int>> roleManager,
            IEmailService emailService,
            ILogger<AccountController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _emailService = emailService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            ViewData["ReturnUrl"] = returnUrl ?? Url.Action("Index", "Dashboard");

            return View(new LoginViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel? model, string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl ?? Url.Action("Index", "Dashboard");

            if (model is null)
            {
                ModelState.AddModelError(string.Empty, "Invalid login request. Please try again.");
                return View(new LoginViewModel());
            }

            // Trim username only — never trim passwords.
            model.Username = (model.Username ?? string.Empty).Trim();

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            try
            {
                var user = await _userManager.FindByNameAsync(model.Username)
                    ?? await _userManager.FindByEmailAsync(model.Username);

                if (user is null)
                {
                    // Generic message on purpose — do not reveal whether the account exists.
                    ModelState.AddModelError(string.Empty, "Invalid username or password.");
                    return View(model);
                }

                var result = await _signInManager.CheckPasswordSignInAsync(
                    user, model.Password ?? string.Empty, lockoutOnFailure: true);

                if (result.Succeeded)
                {
                    // CheckPasswordSignInAsync only verifies the password —
                    // the auth cookie is issued here.
                    await _signInManager.SignInAsync(user, model.RememberMe);
                    _logger.LogInformation("User logged in: {UserName}", user.UserName);

                    if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                    {
                        return Redirect(returnUrl);
                    }

                    return RedirectToAction("Index", "Dashboard");
                }

                if (result.IsLockedOut)
                {
                    _logger.LogWarning("Locked-out login attempt for: {Login}", model.Username);
                    ModelState.AddModelError(string.Empty,
                        "This account is temporarily locked due to too many failed attempts. Please try again later.");
                    return View(model);
                }

                if (result.IsNotAllowed)
                {
                    _logger.LogWarning("Sign-in not allowed for: {Login}", model.Username);
                    ModelState.AddModelError(string.Empty,
                        "This account is not allowed to sign in. Please contact an administrator.");
                    return View(model);
                }

                ModelState.AddModelError(string.Empty, "Invalid username or password.");
                return View(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during login for: {Login}", model.Username);
                ModelState.AddModelError(string.Empty,
                    "An unexpected error occurred while signing in. Please try again.");
                return View(model);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Register()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            ViewData["AvailableRoles"] = await GetAvailableRolesAsync(null);
            return View(new RegisterViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel? model)
        {
            if (model is null)
            {
                ModelState.AddModelError(string.Empty, "Invalid registration request. Please try again.");
                ViewData["AvailableRoles"] = await GetAvailableRolesAsync(null);
                return View(new RegisterViewModel());
            }

            // Normalize input — leading/trailing spaces are a common failure point.
            model.FullName = (model.FullName ?? string.Empty).Trim();
            model.UserName = (model.UserName ?? string.Empty).Trim();
            model.Email = (model.Email ?? string.Empty).Trim();
            model.PhoneNumber = (model.PhoneNumber ?? string.Empty).Trim();
            model.Barangay = string.IsNullOrWhiteSpace(model.Barangay) ? null : model.Barangay.Trim();
            model.City = string.IsNullOrWhiteSpace(model.City) ? null : model.City.Trim();
            model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
            model.Role = string.IsNullOrWhiteSpace(model.Role) ? "Resident" : model.Role.Trim();

            ViewData["AvailableRoles"] = await GetAvailableRolesAsync(model.Role);

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            try
            {
                // Friendly duplicate checks up front (field-level errors, no exception).
                if (await _userManager.FindByNameAsync(model.UserName) is not null)
                {
                    ModelState.AddModelError(nameof(model.UserName),
                        "Username is already taken. Please choose another one.");
                    return View(model);
                }

                if (await _userManager.FindByEmailAsync(model.Email) is not null)
                {
                    ModelState.AddModelError(nameof(model.Email),
                        "Email is already registered. Please use another email or sign in.");
                    return View(model);
                }

                // Validate role — never silently ignore an unknown role.
                var role = model.Role ?? "Resident";
                if (!await _roleManager.RoleExistsAsync(role))
                {
                    _logger.LogWarning("Unknown role '{Role}' during registration; falling back to Resident.", role);
                    role = "Resident";
                }

                var user = new ApplicationUser
                {
                    UserName = model.UserName,
                    Email = model.Email,
                    FullName = model.FullName,
                    EmailConfirmed = true,
                    PhoneNumber = model.PhoneNumber,
                    Barangay = model.Barangay,
                    City = model.City,
                    Address = model.Address,
                    CreatedAt = DateTime.UtcNow
                };

                var result = await _userManager.CreateAsync(user, model.Password ?? string.Empty);

                if (!result.Succeeded)
                {
                    AddIdentityErrorsToModelState(result);
                    return View(model);
                }

                var roleResult = await _userManager.AddToRoleAsync(user, role);
                if (!roleResult.Succeeded)
                {
                    // Roll back so we never leave a role-less account
                    // (Dashboard would bounce it between Login and Index forever).
                    _logger.LogError("Role assignment failed for {UserName}; rolling back account.", model.UserName);
                    await _userManager.DeleteAsync(user);
                    ModelState.AddModelError(string.Empty,
                        "Your account could not be assigned a role. Please try again or contact support.");
                    return View(model);
                }

                _logger.LogInformation("User registered: {UserName} with role {Role}", model.UserName, role);

                await _signInManager.SignInAsync(user, isPersistent: false);
                return RedirectToAction("Index", "Dashboard");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during registration for: {Email}", model.Email);
                ModelState.AddModelError(string.Empty,
                    "An unexpected error occurred while creating your account. Please try again.");
                return View(model);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            try
            {
                var userName = User?.Identity?.Name ?? "Unknown";
                await _signInManager.SignOutAsync();
                _logger.LogInformation("User logged out: {UserName}", userName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during sign-out.");
            }

            return RedirectToAction("Login", "Account");
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPassword()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            return View(new ForgotPasswordViewModel());
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            model.Email = (model.Email ?? string.Empty).Trim();

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Look up the user in PostgreSQL via Identity (normalized email handled
            // by UserManager, case-insensitive). Never reveal existence to the client.
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user is null || user.Email is null)
            {
                // Server log only: distinguishes a DB miss from an SMTP failure.
                _logger.LogWarning("ForgotPassword: no user found for entered email.");
                return RedirectToAction(nameof(ForgotPasswordConfirmation));
            }

            string resetLink;
            try
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                resetLink = Url.Action(
                    nameof(ResetPassword), "Account",
                    new { email = user.Email, token = code },
                    protocol: Request.Scheme) ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForgotPassword: failed generating reset token.");
                return RedirectToAction(nameof(ForgotPasswordConfirmation));
            }

            try
            {
                await _emailService.SendPasswordResetEmailAsync(user.Email, resetLink);
                _logger.LogInformation("ForgotPassword: reset email handed to SMTP provider.");
            }
            catch (EmailServiceException ex)
            {
                // No crash: log the link so local testing can continue without SMTP.
                _logger.LogError(ex, "ForgotPassword: SMTP send failed.");
                _logger.LogWarning("SMTP Failed or Unconfigured. COPY THIS RESET LINK TO TEST: {ResetLink}", resetLink);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForgotPassword: unexpected email error.");
                _logger.LogWarning("SMTP Failed or Unconfigured. COPY THIS RESET LINK TO TEST: {ResetLink}", resetLink);
            }

            return RedirectToAction(nameof(ForgotPasswordConfirmation));
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPasswordConfirmation()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            return View();
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPassword(string? email, string? token)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            {
                return RedirectToAction(nameof(ForgotPassword));
            }

            return View(new AccountResetPasswordViewModel { Email = email, Token = token });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(AccountResetPasswordViewModel model)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            model.Email = (model.Email ?? string.Empty).Trim();
            model.Token = (model.Token ?? string.Empty).Trim();

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user is null)
            {
                // Generic confirmation — do not reveal that the account is missing.
                return RedirectToAction(nameof(ResetPasswordConfirmation));
            }

            string decodedToken;
            try
            {
                decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Token));
            }
            catch
            {
                ModelState.AddModelError(string.Empty, "This reset link is invalid or corrupted. Please request a new one.");
                return View(model);
            }

            var result = await _userManager.ResetPasswordAsync(user, decodedToken, model.NewPassword);
            if (result.Succeeded)
            {
                _logger.LogInformation("Password reset via email link succeeded.");
                return RedirectToAction(nameof(ResetPasswordConfirmation));
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPasswordConfirmation()
        {
            return View();
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            return View(new ProfileViewModel
            {
                UserName = user.UserName ?? string.Empty,
                FullName = user.FullName ?? string.Empty,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                Barangay = user.Barangay,
                City = user.City,
                Address = user.Address
            });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(ProfileViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            model.UserName = user.UserName ?? string.Empty;
            model.FullName = (model.FullName ?? string.Empty).Trim();
            model.Email = (model.Email ?? string.Empty).Trim();
            model.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber.Trim();
            model.Barangay = string.IsNullOrWhiteSpace(model.Barangay) ? null : model.Barangay.Trim();
            model.City = string.IsNullOrWhiteSpace(model.City) ? null : model.City.Trim();
            model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();

            var wantsPasswordChange = !string.IsNullOrWhiteSpace(model.NewPassword);
            if (wantsPasswordChange && string.IsNullOrWhiteSpace(model.CurrentPassword))
            {
                ModelState.AddModelError(nameof(model.CurrentPassword),
                    "Enter your current password to set a new one.");
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var emailOwner = await _userManager.FindByEmailAsync(model.Email);
            if (emailOwner is not null && emailOwner.Id != user.Id)
            {
                ModelState.AddModelError(nameof(model.Email), "This email is already registered to another account.");
                return View(model);
            }

            user.FullName = model.FullName;
            user.Email = model.Email;
            user.PhoneNumber = model.PhoneNumber;
            user.Barangay = model.Barangay;
            user.City = model.City;
            user.Address = model.Address;

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                foreach (var error in updateResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }

            if (wantsPasswordChange)
            {
                var pwdResult = await _userManager.ChangePasswordAsync(
                    user, model.CurrentPassword!, model.NewPassword!);
                if (!pwdResult.Succeeded)
                {
                    foreach (var error in pwdResult.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }

                    return View(model);
                }

                await _signInManager.RefreshSignInAsync(user);
            }

            TempData["SuccessMessage"] = "Your account was updated successfully.";
            return RedirectToAction(nameof(Profile));
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Delete()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            return View(new DeleteAccountViewModel
            {
                UserName = user.UserName ?? string.Empty,
                Email = user.Email ?? string.Empty
            });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(DeleteAccountViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            if (!ModelState.IsValid)
            {
                model.UserName = user.UserName ?? string.Empty;
                model.Email = user.Email ?? string.Empty;
                return View(model);
            }

            if (!await _userManager.CheckPasswordAsync(user, model.Password))
            {
                ModelState.AddModelError(nameof(model.Password), "Incorrect password.");
                model.UserName = user.UserName ?? string.Empty;
                model.Email = user.Email ?? string.Empty;
                return View(model);
            }

            // Safety: never delete the last remaining Admin.
            if (await _userManager.IsInRoleAsync(user, "Admin"))
            {
                var admins = await _userManager.GetUsersInRoleAsync("Admin");
                if (admins.Count <= 1)
                {
                    ModelState.AddModelError(string.Empty,
                        "You are the last Admin and cannot delete this account. Promote another user to Admin first.");
                    model.UserName = user.UserName ?? string.Empty;
                    model.Email = user.Email ?? string.Empty;
                    return View(model);
                }
            }

            var userName = user.UserName;
            await _signInManager.SignOutAsync();
            await _userManager.DeleteAsync(user);
            _logger.LogInformation("User deleted own account: {UserName}", userName);
            TempData["SuccessMessage"] = "Your account has been deleted.";
            return RedirectToAction(nameof(Login));
        }

        private async Task<SelectList> GetAvailableRolesAsync(string? selectedRole)
        {
            try
            {
                var roleNames = await _roleManager.Roles
                    .Where(r => r.Name != "Admin" && r.Name != "LGU" && r.Name != "Barangay" && r.Name != "Maintenance")
                    .Select(r => r.Name!)
                    .ToListAsync();

                if (roleNames.Count == 0)
                {
                    roleNames = new List<string> { "Resident" };
                }

                return new SelectList(roleNames, selectedRole);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load available roles; defaulting to Resident.");
                return new SelectList(new List<string> { "Resident" }, selectedRole);
            }
        }

        private void AddIdentityErrorsToModelState(IdentityResult result)
        {
            foreach (var error in result.Errors)
            {
                // Map Identity error codes to the right form field so the
                // message shows next to the offending input.
                var key = error.Code switch
                {
                    "DuplicateUserName" => nameof(RegisterViewModel.UserName),
                    "InvalidUserName" => nameof(RegisterViewModel.UserName),
                    "DuplicateEmail" => nameof(RegisterViewModel.Email),
                    "InvalidEmail" => nameof(RegisterViewModel.Email),
                    _ when error.Code.StartsWith("Password", StringComparison.OrdinalIgnoreCase)
                        => nameof(RegisterViewModel.Password),
                    _ => string.Empty
                };

                ModelState.AddModelError(key, error.Description);
            }
        }
    }
}
