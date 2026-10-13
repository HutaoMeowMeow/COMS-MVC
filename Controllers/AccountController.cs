using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace COMS_MVC.Controllers
{
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole<int>> _roleManager;
        private readonly IEmailService _emailService;
        private readonly IOtpService _otpService;
        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly ILogger<AccountController> _logger;

        public AccountController(UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole<int>> roleManager,
            IEmailService emailService,
            IOtpService otpService,
            ApplicationDbContext db,
            IMemoryCache cache,
            ILogger<AccountController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _emailService = emailService;
            _otpService = otpService;
            _db = db;
            _cache = cache;
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
                    // Unverified EXISTING account (correct password, email not
                    // confirmed): never send it through registration. Seed or
                    // refresh a pending OTP from the existing record so the
                    // owner can prove the address without creating a second
                    // account, then continue through VerifyEmail.
                    if (!user.EmailConfirmed)
                    {
                        var pendingForUser = await EnsurePendingForExistingUserAsync(user);
                        var otpForUser = await _otpService.IssueAsync(pendingForUser);
                        await _db.SaveChangesAsync();

                        if (!otpForUser.Succeeded)
                        {
                            TempData["ErrorMessage"] = otpForUser.Error;
                        }
                        else
                        {
                            TempData["AlertMessage"] = "Please verify your email with the code we sent before signing in.";
                        }
                        return RedirectToAction(nameof(VerifyEmail), new { email = pendingForUser.Email });
                    }

                    // Successful login sets no TempData of its own. Drop any
                    // stale registration/verification messages left unread in
                    // the TempData cookie (e.g. from an earlier anonymous
                    // VerifyEmail -> Register chain, whose pages never render
                    // them) so they can never surface on the dashboard.
                    TempData.Remove("ErrorMessage");
                    TempData.Remove("AlertMessage");

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
            model.PhoneNumber = NormalizePhone(model.PhoneNumber);
            model.Barangay = string.IsNullOrWhiteSpace(model.Barangay) ? null : model.Barangay.Trim();
            model.City = string.IsNullOrWhiteSpace(model.City) ? null : model.City.Trim();
            model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
            model.Role = string.IsNullOrWhiteSpace(model.Role) ? "Resident" : model.Role.Trim();

            ViewData["AvailableRoles"] = await GetAvailableRolesAsync(model.Role);

            // DataAnnotations run during model binding on the RAW values, so
            // re-validate here against the NORMALIZED values. Invalid data
            // returns to the registration page — no pending row, no OTP.
            ModelState.Clear();
            var normalizedResults = new List<ValidationResult>();
            Validator.TryValidateObject(
                model, new ValidationContext(model), normalizedResults, validateAllProperties: true);
            foreach (var vr in normalizedResults)
            {
                var members = vr.MemberNames.DefaultIfEmpty(string.Empty);
                foreach (var name in members)
                {
                    ModelState.AddModelError(name, vr.ErrorMessage ?? "Invalid value.");
                }
            }
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            try
            {
                // Real accounts first: an already-verified email/username can never re-register.
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

                // Validate role against the self-registration allowlist — the same
                // set offered in the dropdown (never Admin/LGU/Barangay/
                // Maintenance). A crafted POST with Role=Admin must not create
                // a privileged account; unknown or privileged roles fall back
                // to Resident.
                var role = model.Role ?? "Resident";
                if (!await IsSelfRegistrableRoleAsync(role))
                {
                    _logger.LogWarning("Rejected self-registration role '{Role}'; falling back to Resident.", role);
                    role = "Resident";
                }

                // Validate the username AND password with the same Identity
                // validators used at account creation, without creating any
                // User row yet. This is what rejects e.g. "MANOK MANOK" here
                // on the registration page instead of on the OTP page.
                var probe = new ApplicationUser { UserName = model.UserName, Email = model.Email };
                foreach (var validator in _userManager.UserValidators)
                {
                    var vr = await validator.ValidateAsync(_userManager, probe);
                    if (!vr.Succeeded)
                    {
                        foreach (var error in vr.Errors)
                        {
                            ModelState.AddModelError(nameof(model.UserName), error.Description);
                        }
                        return View(model);
                    }
                }
                foreach (var validator in _userManager.PasswordValidators)
                {
                    var vr = await validator.ValidateAsync(_userManager, probe, model.Password ?? string.Empty);
                    if (!vr.Succeeded)
                    {
                        foreach (var error in vr.Errors)
                        {
                            ModelState.AddModelError(nameof(model.Password), error.Description);
                        }
                        return View(model);
                    }
                }

                await CleanupExpiredPendingsAsync();

                var normalizedEmail = _userManager.NormalizeEmail(model.Email);
                var normalizedUserName = _userManager.NormalizeName(model.UserName);

                // A different pending registration already holds this username.
                var nameClash = await _db.PendingRegistrations
                    .FirstOrDefaultAsync(p => p.NormalizedUserName == normalizedUserName
                        && p.NormalizedEmail != normalizedEmail);
                if (nameClash is not null)
                {
                    ModelState.AddModelError(nameof(model.UserName),
                        "Username is already taken. Please choose another one.");
                    return View(model);
                }

                // Same email re-registering before verifying: reuse the pending row
                // (new details + fresh OTP) instead of stacking duplicates.
                var pending = await _db.PendingRegistrations
                    .FirstOrDefaultAsync(p => p.NormalizedEmail == normalizedEmail);

                var passwordHash = _userManager.PasswordHasher.HashPassword(probe, model.Password ?? string.Empty);

                if (pending is null)
                {
                    var now = DateTime.UtcNow;
                    pending = new PendingRegistration
                    {
                        Email = model.Email,
                        NormalizedEmail = normalizedEmail,
                        UserName = model.UserName,
                        NormalizedUserName = normalizedUserName,
                        CreatedAtUtc = now,
                        ExpiresAtUtc = now.Add(OtpService.PendingLifetime)
                    };
                    _db.PendingRegistrations.Add(pending);
                }

                pending.UserName = model.UserName;
                pending.NormalizedUserName = normalizedUserName;
                pending.FullName = model.FullName;
                pending.PhoneNumber = model.PhoneNumber;
                pending.Barangay = model.Barangay;
                pending.City = model.City;
                pending.Address = model.Address;
                pending.Role = role;
                pending.PasswordHash = passwordHash;

                // NO ApplicationUser row is created here — Admin only ever sees
                // verified users. The real account is created in VerifyEmail.
                var otp = await _otpService.IssueAsync(pending);
                await _db.SaveChangesAsync();

                _logger.LogInformation("Pending registration stored for role {Role} (no user account created yet).", role);

                if (!otp.Succeeded)
                {
                    TempData["ErrorMessage"] = otp.Error;
                    return RedirectToAction(nameof(VerifyEmail), new { email = pending.Email });
                }

                TempData["SuccessMessage"] = "Verification code sent to your email.";
                return RedirectToAction(nameof(VerifyEmail), new { email = pending.Email });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during registration for: {Email}", model.Email);
                ModelState.AddModelError(string.Empty,
                    "An unexpected error occurred while creating your account. Please try again.");
                return View(model);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> VerifyEmail(string? email)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                return RedirectToAction(nameof(Register));
            }

            await CleanupExpiredPendingsAsync();

            // No User row exists yet at this stage by design — only a pending row.
            var pending = await FindPendingAsync(email.Trim());
            if (pending is null)
            {
                // An existing account has no pending row. Confirmed accounts
                // must sign in, never re-register; unconfirmed legacy accounts
                // get an OTP seeded from their own record (no duplicate, no
                // registration from scratch).
                var existing = await _userManager.FindByEmailAsync(email.Trim());
                if (existing is not null && existing.EmailConfirmed)
                {
                    TempData["AlertMessage"] = "This email is already verified. Please sign in.";
                    return RedirectToAction(nameof(Login));
                }
                if (existing is not null && !existing.EmailConfirmed)
                {
                    var seeded = await EnsurePendingForExistingUserAsync(existing);
                    var seededOtp = await _otpService.IssueAsync(seeded);
                    await _db.SaveChangesAsync();
                    if (!seededOtp.Succeeded)
                    {
                        TempData["ErrorMessage"] = seededOtp.Error;
                    }
                    else
                    {
                        TempData["AlertMessage"] = "Please verify your email with the code we sent before signing in.";
                    }
                    return View(new VerifyOtpViewModel { Email = seeded.Email });
                }

                TempData["ErrorMessage"] = "No pending registration found for this email. Please register first.";
                return RedirectToAction(nameof(Register));
            }

            return View(new VerifyOtpViewModel { Email = pending.Email });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyEmail(VerifyOtpViewModel model)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            model.Email = (model.Email ?? string.Empty).Trim();
            model.Code = (model.Code ?? string.Empty).Trim();

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            await CleanupExpiredPendingsAsync();

            var pending = await FindPendingAsync(model.Email);
            if (pending is null)
            {
                ModelState.AddModelError(string.Empty, "No pending registration found for this email. Please register first.");
                return View(model);
            }

            var check = await _otpService.VerifyAsync(pending, model.Code);
            if (!check.Succeeded)
            {
                // Wrong/expired code: nothing is created. Persist attempts only.
                await _db.SaveChangesAsync();
                ModelState.AddModelError(nameof(model.Code), check.Error);
                return View(model);
            }

            // Correct OTP: create the REAL user account atomically, then drop
            // the pending row. A failure anywhere rolls everything back, so we
            // never leave a half-created account or a reused registration.
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                // The pending address may already belong to a real account:
                // a legacy unverified account seeded its own pending above,
                // or the address was taken after the OTP was issued. A
                // confirmed address can never verify again — sign in instead.
                // An unconfirmed address with a matching username is the same
                // owner proving the address: confirm it, never duplicate it.
                // Anything else is a genuine collision.
                var existingByEmail = await _userManager.FindByEmailAsync(pending.Email);
                if (existingByEmail is not null)
                {
                    if (existingByEmail.EmailConfirmed)
                    {
                        await tx.RollbackAsync();
                        ModelState.AddModelError(string.Empty,
                            "This email or username was registered while you were verifying. Please sign in instead.");
                        return View(model);
                    }

                    var nameOwner = await _userManager.FindByNameAsync(pending.UserName);
                    if (nameOwner is not null && nameOwner.Id != existingByEmail.Id)
                    {
                        await tx.RollbackAsync();
                        ModelState.AddModelError(string.Empty,
                            "This username is already taken. Please contact an administrator.");
                        return View(model);
                    }

                    existingByEmail.EmailConfirmed = true;
                    var confirmResult = await _userManager.UpdateAsync(existingByEmail);
                    if (!confirmResult.Succeeded)
                    {
                        await tx.RollbackAsync();
                        foreach (var error in confirmResult.Errors)
                        {
                            ModelState.AddModelError(string.Empty, error.Description);
                        }
                        return View(model);
                    }

                    _db.PendingRegistrations.Remove(pending);
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();

                    _logger.LogInformation("Existing account email verified via OTP; no duplicate account created.");
                    TempData["SuccessMessage"] = "Your email has been verified. You can now sign in.";
                    return RedirectToAction(nameof(Login));
                }

                if (await _userManager.FindByNameAsync(pending.UserName) is not null)
                {
                    await tx.RollbackAsync();
                    ModelState.AddModelError(string.Empty,
                        "This email or username was registered while you were verifying. Please sign in instead.");
                    return View(model);
                }

                var role = pending.Role;
                // Re-validate at verification time: a pending row crafted or
                // tampered to hold a staff role must never activate as staff.
                if (!await IsSelfRegistrableRoleAsync(role))
                {
                    role = "Resident";
                }

                var user = new ApplicationUser
                {
                    UserName = pending.UserName,
                    Email = pending.Email,
                    FullName = pending.FullName,
                    EmailConfirmed = true,
                    PhoneNumber = pending.PhoneNumber,
                    Barangay = pending.Barangay,
                    City = pending.City,
                    Address = pending.Address,
                    PasswordHash = pending.PasswordHash,
                    CreatedAt = DateTime.UtcNow
                };

                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    await tx.RollbackAsync();
                    foreach (var error in createResult.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }
                    return View(model);
                }

                var roleResult = await _userManager.AddToRoleAsync(user, role);
                if (!roleResult.Succeeded)
                {
                    await tx.RollbackAsync();
                    _logger.LogError("Role assignment failed at OTP verification; no account created.");
                    ModelState.AddModelError(string.Empty,
                        "Your account could not be activated. Please try verifying again.");
                    return View(model);
                }

                _db.PendingRegistrations.Remove(pending);
                await _db.SaveChangesAsync();
                await tx.CommitAsync();

                _logger.LogInformation("Email OTP verified; user account created with role {Role}.", role);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError(ex, "Failed to create user account at OTP verification.");
                ModelState.AddModelError(string.Empty,
                    "Your account could not be activated. Please try verifying again.");
                return View(model);
            }

            TempData["SuccessMessage"] = "Account successfully verified. You can now sign in.";
            return RedirectToAction(nameof(Login));
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendOtp(string? email)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["ErrorMessage"] = "Email address is required to resend the code.";
                return RedirectToAction(nameof(Register));
            }

            await CleanupExpiredPendingsAsync();

            var pending = await FindPendingAsync(email.Trim());
            if (pending is null)
            {
                // Same rule as VerifyEmail GET: confirmed accounts sign in,
                // unconfirmed legacy accounts get an OTP from their own
                // record, unknown addresses go back to registration.
                var existing = await _userManager.FindByEmailAsync(email.Trim());
                if (existing is not null && existing.EmailConfirmed)
                {
                    TempData["AlertMessage"] = "This email is already verified. Please sign in.";
                    return RedirectToAction(nameof(Login));
                }
                if (existing is not null && !existing.EmailConfirmed)
                {
                    pending = await EnsurePendingForExistingUserAsync(existing);
                }
                else
                {
                    TempData["ErrorMessage"] = "No pending registration found for this email. Please register first.";
                    return RedirectToAction(nameof(Register));
                }
            }

            var otp = await _otpService.IssueAsync(pending);
            await _db.SaveChangesAsync();

            if (!otp.Succeeded)
            {
                TempData["ErrorMessage"] = otp.Error;
            }
            else
            {
                TempData["SuccessMessage"] = "Verification code sent to your email.";
            }

            return RedirectToAction(nameof(VerifyEmail), new { email = pending.Email });
        }

        /// <summary>Deletes pending registrations older than 24h. Real users are never touched.</summary>
        private async Task CleanupExpiredPendingsAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                await _db.PendingRegistrations
                    .Where(p => p.ExpiresAtUtc < now)
                    .ExecuteDeleteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clean up expired pending registrations.");
            }
        }

        private Task<PendingRegistration?> FindPendingAsync(string email)
        {
            var normalized = _userManager.NormalizeEmail(email);
            return _db.PendingRegistrations
                .FirstOrDefaultAsync(p => p.NormalizedEmail == normalized);
        }

        /// <summary>
        /// Returns the pending OTP row for a legacy unverified account,
        /// creating it from the existing user record when none exists. The
        /// real account (password, roles) is never modified here — the row is
        /// only a vehicle for the OTP hash. Verification consumes it in
        /// VerifyEmail POST by marking the existing account confirmed.
        /// </summary>
        private async Task<PendingRegistration> EnsurePendingForExistingUserAsync(ApplicationUser user)
        {
            await CleanupExpiredPendingsAsync();

            var email = (user.Email ?? string.Empty).Trim();
            var normalizedEmail = _userManager.NormalizeEmail(email);
            var pending = await _db.PendingRegistrations
                .FirstOrDefaultAsync(p => p.NormalizedEmail == normalizedEmail);
            if (pending is not null)
            {
                return pending;
            }

            // The real account owns its username: a squatter pending holding
            // the same name could never verify (creation would be rejected as
            // a duplicate), so it is dropped in favor of the real owner.
            var userName = (user.UserName ?? email).Trim();
            var normalizedUserName = _userManager.NormalizeName(userName);
            var squatter = await _db.PendingRegistrations
                .FirstOrDefaultAsync(p => p.NormalizedUserName == normalizedUserName
                    && p.NormalizedEmail != normalizedEmail);
            if (squatter is not null)
            {
                _logger.LogWarning("Removing squatter pending registration for username '{UserName}' owned by an existing account.", userName);
                _db.PendingRegistrations.Remove(squatter);
            }

            var now = DateTime.UtcNow;
            var roles = await _userManager.GetRolesAsync(user);
            pending = new PendingRegistration
            {
                Email = email,
                NormalizedEmail = normalizedEmail,
                UserName = userName,
                NormalizedUserName = normalizedUserName,
                FullName = user.FullName ?? userName,
                PhoneNumber = user.PhoneNumber,
                Barangay = user.Barangay,
                City = user.City,
                Address = user.Address,
                Role = roles.FirstOrDefault() ?? "Resident",
                PasswordHash = user.PasswordHash ?? string.Empty,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(OtpService.PendingLifetime)
            };
            _db.PendingRegistrations.Add(pending);
            return pending;
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
            // by UserManager, case-insensitive). Missing emails get an explicit
            // "not found" error on the form (per requirements, no silent generic
            // confirmation for unknown addresses).
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user is null || user.Email is null)
            {
                if (!CheckAbuseThrottle("forgot-pw-ip:" + ClientIp(), out var ipError))
                {
                    _logger.LogWarning("ForgotPassword throttled for unknown email from {Ip}.", ClientIp());
                    ModelState.AddModelError(string.Empty, ipError ?? "Please try again later.");
                    return View(model);
                }
                _logger.LogWarning("ForgotPassword: no user found for entered email.");
                ModelState.AddModelError(nameof(model.Email),
                    "Email not found. Please check the address or register a new account.");
                return View(model);
            }

            // Abuse throttle for known addresses: 60s cooldown, max 5/hour.
            // Throttled requests stay on the form with an honest message; no
            // token is minted and no email is sent.
            if (!CheckAbuseThrottle("forgot-pw:" + _userManager.NormalizeEmail(model.Email), out var throttleError))
            {
                _logger.LogWarning("ForgotPassword throttled for entered email.");
                ModelState.AddModelError(string.Empty, throttleError ?? "Please try again later.");
                return View(model);
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

            // Email changes require proof of ownership: the new address is
            // NOT applied here. A confirmation link is sent to it instead;
            // the current (verified) address keeps working until then.
            var emailChanged = !string.Equals(
                user.Email, model.Email, StringComparison.OrdinalIgnoreCase);
            var newEmail = model.Email;

            user.FullName = model.FullName;
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

                // New stamp signs out every OTHER session still holding the
                // old password; the current one is re-issued below.
                await _userManager.UpdateSecurityStampAsync(user);
                await _signInManager.RefreshSignInAsync(user);
            }

            if (emailChanged)
            {
                try
                {
                    var token = await _userManager.GenerateChangeEmailTokenAsync(user, newEmail);
                    var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                    var link = Url.Action(
                        nameof(ConfirmEmailChange), "Account",
                        new { userId = user.Id, email = newEmail, token = code },
                        protocol: Request.Scheme) ?? string.Empty;
                    var safeLink = WebUtility.HtmlEncode(link);
                    await _emailService.SendEmailAsync(newEmail, "Confirm your new COMS email",
                        $"""
                        <div style="font-family:Arial,sans-serif;max-width:560px;margin:0 auto;">
                          <h2>Confirm your new email</h2>
                          <p>You requested to change the email on your COMS account to this address.</p>
                          <p style="margin:24px 0;">
                            <a href="{safeLink}" style="display:inline-block;padding:12px 24px;background:#2b6cb0;color:#fff;text-decoration:none;border-radius:8px;">Confirm email</a>
                          </p>
                          <p style="font-size:12px;word-break:break-all;color:#555;">{safeLink}</p>
                          <p style="color:#555;font-size:13px;">If you did not request this, you can safely ignore this email — your account keeps its current address.</p>
                          <p>— Canal Obstruction Maintenance System</p>
                        </div>
                        """);
                    TempData["SuccessMessage"] = "Profile saved. A confirmation link was sent to your new email — it takes effect only after you confirm it.";
                }
                catch (EmailServiceException ex)
                {
                    _logger.LogError(ex, "Email-change confirmation send failed.");
                    TempData["SuccessMessage"] = "Profile saved, but the confirmation email could not be sent. Your email address was not changed.";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected email-change error.");
                    TempData["SuccessMessage"] = "Profile saved, but the confirmation email could not be sent. Your email address was not changed.";
                }
                return RedirectToAction(nameof(Profile));
            }

            TempData["SuccessMessage"] = "Your account was updated successfully.";
            return RedirectToAction(nameof(Profile));
        }

        /// <summary>
        /// Applies a pending email change after proving ownership of the new
        /// address. The confirmer must be signed in AS the same account
        /// (userId is never trusted from the link alone).
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ConfirmEmailChange(int userId, string? email, string? token)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null || user.Id != userId)
            {
                return RedirectToAction(nameof(Login));
            }
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            {
                TempData["ErrorMessage"] = "This confirmation link is incomplete. Please request a new one from your profile.";
                return RedirectToAction(nameof(Profile));
            }
            email = email.Trim();
            string decodedToken;
            try
            {
                decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token.Trim()));
            }
            catch
            {
                TempData["ErrorMessage"] = "This confirmation link is invalid. Please request a new one from your profile.";
                return RedirectToAction(nameof(Profile));
            }

            var owner = await _userManager.FindByEmailAsync(email);
            if (owner is not null && owner.Id != user.Id)
            {
                TempData["ErrorMessage"] = "This email is already registered to another account.";
                return RedirectToAction(nameof(Profile));
            }

            var result = await _userManager.ChangeEmailAsync(user, email, decodedToken);
            if (!result.Succeeded)
            {
                TempData["ErrorMessage"] = "This confirmation link is invalid or expired. Please request a new one from your profile.";
                return RedirectToAction(nameof(Profile));
            }

            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);
            await _userManager.UpdateSecurityStampAsync(user);
            await _signInManager.RefreshSignInAsync(user);
            TempData["SuccessMessage"] = "Your email address has been updated and confirmed.";
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
            IdentityResult deleteResult;
            try
            {
                // Delete first while still signed in: if it fails (e.g. the
                // account owns records blocked by Restrict FKs), the user
                // stays signed in and sees the reason instead of being
                // signed out with the account still intact.
                deleteResult = await _userManager.DeleteAsync(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting own account for: {UserName}", userName);
                ModelState.AddModelError(string.Empty,
                    "Your account could not be deleted because it still owns records (reports, tickets, or announcements). Please delete those first or contact an administrator.");
                model.UserName = user.UserName ?? string.Empty;
                model.Email = user.Email ?? string.Empty;
                return View(model);
            }

            if (!deleteResult.Succeeded)
            {
                foreach (var error in deleteResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
                model.UserName = user.UserName ?? string.Empty;
                model.Email = user.Email ?? string.Empty;
                return View(model);
            }

            await _signInManager.SignOutAsync();
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

        /// <summary>
        /// Normalizes a Philippine mobile number for storage: strips spaces,
        /// dashes, dots, and parentheses only — digits (and a leading +) are
        /// never altered, so a valid number is never silently changed into a
        /// different number. "09…" and "+63…" forms are both kept as entered.
        /// </summary>
        internal static string NormalizePhone(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            var stripped = Regex.Replace(value.Trim(), @"[\s\-\.\(\)]", string.Empty);
            return stripped;
        }

        /// <summary>
        /// Self-registration allowlist: only roles the signup form offers.
        /// Staff roles (Admin/LGU/Barangay/Maintenance) can only be granted
        /// by an Admin via Users/Edit — never via crafted registration POSTs.
        /// </summary>
        private async Task<bool> IsSelfRegistrableRoleAsync(string role)
        {
            try
            {
                var allowed = await _roleManager.Roles
                    .Where(r => r.Name != "Admin" && r.Name != "LGU" && r.Name != "Barangay" && r.Name != "Maintenance")
                    .Select(r => r.Name!)
                    .ToListAsync();
                if (allowed.Count == 0)
                {
                    allowed = new List<string> { "Resident" };
                }
                return allowed.Contains(role);
            }
            catch
            {
                return role == "Resident";
            }
        }

        /// <summary>
        /// Abuse throttle for forgot-password: 60-second cooldown and max 5
        /// requests per hour per key (email or IP). Backed by IMemoryCache;
        /// legitimate users are unaffected, automated email-bombing is not.
        /// </summary>
        private bool CheckAbuseThrottle(string key, out string? error)
        {
            error = null;
            var now = DateTime.UtcNow;
            var lastKey = key + ":last";
            var countKey = key + ":count";
            var windowKey = key + ":window";
            if (_cache.TryGetValue<DateTime>(lastKey, out var last)
                && now - last < TimeSpan.FromSeconds(60))
            {
                var wait = (int)Math.Ceiling(60 - (now - last).TotalSeconds);
                error = $"Please wait {wait} seconds before trying again.";
                return false;
            }
            var count = _cache.GetOrCreate(countKey, e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return 0;
            });
            var windowStart = _cache.GetOrCreate(windowKey, e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return now;
            });
            if (now - windowStart > TimeSpan.FromHours(1))
            {
                count = 0;
                windowStart = now;
                _cache.Set(windowKey, windowStart, TimeSpan.FromHours(1));
                _cache.Set(countKey, count, TimeSpan.FromHours(1));
            }
            if (count >= 5)
            {
                error = "Too many requests. Please try again later.";
                return false;
            }
            _cache.Set(countKey, count + 1, TimeSpan.FromHours(1));
            _cache.Set(lastKey, now, TimeSpan.FromSeconds(60));
            return true;
        }

        private string ClientIp() =>
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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
