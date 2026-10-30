using COMS_MVC.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
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
        private readonly IWebHostEnvironment _env;
        private readonly ITrustedDeviceService? _trustedDevices;
        private readonly TwoFactorOptions _twoFactorOptions;

        private const string TwoFactorAttemptCachePrefix = "2fa-attempt";
        private const int TwoFactorMaxAttempts = 5;
        private static readonly TimeSpan TwoFactorAttemptWindow = TimeSpan.FromMinutes(10);

        public AccountController(UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole<int>> roleManager,
            IEmailService emailService,
            IOtpService otpService,
            ApplicationDbContext db,
            IMemoryCache cache,
            ILogger<AccountController> logger,
            IWebHostEnvironment env,
            ITrustedDeviceService? trustedDevices = null,
            Microsoft.Extensions.Options.IOptions<TwoFactorOptions>? twoFactorOptions = null)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _emailService = emailService;
            _otpService = otpService;
            _db = db;
            _cache = cache;
            _logger = logger;
            _env = env;
            _trustedDevices = trustedDevices;
            _twoFactorOptions = twoFactorOptions?.Value ?? new TwoFactorOptions();
        }

        private sealed class TwoFactorChallenge
        {
            public int UserId { get; set; }
            public string? ReturnUrl { get; set; }
            public bool RememberMe { get; set; }
            public int Attempts { get; set; }
        }

        private IActionResult RedirectAfterLogin(string? returnUrl)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Dashboard");
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
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Login(LoginViewModel? model, string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl ?? Url.Action("Index", "Dashboard");

            if (model is null)
            {
                ModelState.AddModelError(string.Empty, "Invalid login request. Please try again.");
                return View(new LoginViewModel());
            }

            // Trim username only â€” never trim passwords.
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
                    // Generic message on purpose â€” do not reveal whether the account exists.
                    ModelState.AddModelError(string.Empty, "Invalid username or password.");
                    return View(model);
                }

                var result = await _signInManager.CheckPasswordSignInAsync(
                    user, model.Password ?? string.Empty, lockoutOnFailure: true);

                if (result.Succeeded)
                {
                    // New registrations must verify email OTP before first sign-in.
                    // Existing (already-confirmed) accounts are unaffected.
                    if (!user.EmailConfirmed)
                    {
                        TempData["AlertMessage"] = "Please verify your email with the code we sent before signing in.";
                        return RedirectToAction(nameof(VerifyEmail), new { email = user.Email });
                    }

                    if (user.TwoFactorEnabled)
                    {
                        // A valid, unexpired trusted-device token skips the
                        // authenticator prompt for this browser/device only.
                        var trustedToken = TrustedDeviceCookie.Read(HttpContext);
                        if (_trustedDevices is not null
                            && await _trustedDevices.HasValidTrustedDeviceAsync(user.Id, trustedToken))
                        {
                            await _trustedDevices.TouchAsync(user.Id, trustedToken!);
                            await _signInManager.SignInAsync(user, model.RememberMe);
                            _logger.LogInformation("User logged in with trusted device: {UserName}", user.UserName);
                            return RedirectAfterLogin(returnUrl);
                        }

                        // Do not issue the auth cookie until the second factor
                        // is verified. The short-lived challenge ID is opaque
                        // and single-use.
                        var challengeId = Guid.NewGuid().ToString("N");
                        _cache.Set(
                            TwoFactorAttemptCachePrefix + ":" + challengeId,
                            new TwoFactorChallenge
                            {
                                UserId = user.Id,
                                ReturnUrl = returnUrl,
                                RememberMe = model.RememberMe
                            },
                            TwoFactorAttemptWindow);

                        _logger.LogInformation("Two-factor challenge issued for: {UserName}", user.UserName);
                        return RedirectToAction(nameof(TwoFactor), new { challenge = challengeId });
                    }

                    // CheckPasswordSignInAsync only verifies the password �?"
                    // the auth cookie is issued here.
                    await _signInManager.SignInAsync(user, model.RememberMe);
                    _logger.LogInformation("User logged in: {UserName}", user.UserName);

                    return RedirectAfterLogin(returnUrl);
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
        [AllowAnonymous]
        public async Task<IActionResult> TwoFactor(string? challenge = null)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            if (!TryGetChallenge(challenge, out var challengeId, out var pending))
            {
                TempData["ErrorMessage"] = "Your two-factor sign-in session expired. Please sign in again.";
                return RedirectToAction(nameof(Login));
            }

            var user = await _userManager.FindByIdAsync(pending.UserId.ToString());
            if (user is null || !user.TwoFactorEnabled)
            {
                _cache.Remove(ChallengeCacheKey(challengeId));
                return RedirectToAction(nameof(Login));
            }

            ViewBag.TrustedDeviceDays = _twoFactorOptions.TrustedDeviceDays;
            return View(new TwoFactorLoginViewModel
            {
                ChallengeId = challengeId,
                ReturnUrl = pending.ReturnUrl,
                RememberMe = pending.RememberMe
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> TwoFactor(TwoFactorLoginViewModel model)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Dashboard");
            }

            if (!TryGetChallenge(model.ChallengeId, out var challengeId, out var pending))
            {
                TempData["ErrorMessage"] = "Your two-factor sign-in session expired. Please sign in again.";
                return RedirectToAction(nameof(Login));
            }

            var user = await _userManager.FindByIdAsync(pending.UserId.ToString());
            if (user is null || !user.TwoFactorEnabled)
            {
                _cache.Remove(ChallengeCacheKey(challengeId));
                return RedirectToAction(nameof(Login));
            }

            model.ChallengeId = challengeId;
            model.ReturnUrl = pending.ReturnUrl;
            model.RememberMe = pending.RememberMe;
            ViewBag.TrustedDeviceDays = _twoFactorOptions.TrustedDeviceDays;

            var code = (model.Code ?? string.Empty).Trim();
            if (!ModelState.IsValid || string.IsNullOrWhiteSpace(code))
            {
                return View(model);
            }

            var totpCode = new string(code.Where(char.IsDigit).ToArray());
            var verified = false;

            if (totpCode.Length == 6 && code.All(c => char.IsDigit(c) || char.IsWhiteSpace(c)))
            {
                verified = await _userManager.VerifyTwoFactorTokenAsync(
                    user, TokenOptions.DefaultAuthenticatorProvider, totpCode);
            }

            if (!verified)
            {
                var recoveryResult = await _userManager.RedeemTwoFactorRecoveryCodeAsync(
                    user, NormalizeRecoveryCode(code));
                verified = recoveryResult.Succeeded;
            }

            if (!verified)
            {
                pending.Attempts++;
                if (pending.Attempts >= TwoFactorMaxAttempts)
                {
                    _cache.Remove(ChallengeCacheKey(challengeId));
                    _logger.LogWarning("Two-factor challenge locked after too many attempts for user {UserId}.", user.Id);
                    TempData["ErrorMessage"] = "Too many incorrect codes. Please sign in again.";
                    return RedirectToAction(nameof(Login));
                }

                _cache.Set(ChallengeCacheKey(challengeId), pending, TwoFactorAttemptWindow);
                ModelState.AddModelError(nameof(model.Code), "Invalid authenticator or recovery code.");
                return View(model);
            }

            _cache.Remove(ChallengeCacheKey(challengeId));
            await _userManager.ResetAccessFailedCountAsync(user);
            await _signInManager.SignInAsync(user, pending.RememberMe);

            if (model.RememberDevice && _trustedDevices is not null)
            {
                var token = TrustedDeviceCookie.NewToken();
                var deviceName = string.IsNullOrWhiteSpace(model.DeviceName)
                    ? BuildDeviceName()
                    : model.DeviceName.Trim();
                await _trustedDevices.CreateAsync(
                    user.Id, deviceName, Request.Headers.UserAgent.ToString(), token);
                TrustedDeviceCookie.Write(HttpContext, token, _twoFactorOptions.TrustedDeviceDays);
            }

            _logger.LogInformation("User logged in with two-factor authentication: {UserName}", user.UserName);
            return RedirectAfterLogin(pending.ReturnUrl);
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> EnableTwoFactor()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            if (user.TwoFactorEnabled)
            {
                TempData["AlertMessage"] = "Two-factor authentication is already enabled.";
                return RedirectToAction(nameof(TrustedDevices));
            }

            return View(await BuildEnableTwoFactorViewModel(user));
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnableTwoFactor(EnableTwoFactorViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            if (!ModelState.IsValid)
            {
                return View(await BuildEnableTwoFactorViewModel(user, model.Code));
            }

            var key = await _userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrWhiteSpace(key))
            {
                await _userManager.ResetAuthenticatorKeyAsync(user);
                key = await _userManager.GetAuthenticatorKeyAsync(user);
            }

            var code = (model.Code ?? string.Empty).Trim();
            var verified = !string.IsNullOrWhiteSpace(key)
                && await _userManager.VerifyTwoFactorTokenAsync(
                    user, TokenOptions.DefaultAuthenticatorProvider, code);

            if (!verified)
            {
                ModelState.AddModelError(nameof(model.Code),
                    "That code is not valid. Check your device clock and try the current code.");
                return View(await BuildEnableTwoFactorViewModel(user, model.Code));
            }

            var enableResult = await _userManager.SetTwoFactorEnabledAsync(user, true);
            if (!enableResult.Succeeded)
            {
                foreach (var error in enableResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
                return View(await BuildEnableTwoFactorViewModel(user, model.Code));
            }

            var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            await _signInManager.RefreshSignInAsync(user);
            _logger.LogInformation("Two-factor authentication enabled for user {UserId}.", user.Id);

            return View("TwoFactorRecoveryCodes", new TwoFactorRecoveryCodesViewModel
            {
                RecoveryCodes = recoveryCodes?.ToList() ?? new List<string>()
            });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DisableTwoFactor()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            await _userManager.SetTwoFactorEnabledAsync(user, false);
            await _userManager.ResetAuthenticatorKeyAsync(user);
            await _userManager.UpdateSecurityStampAsync(user);
            await _signInManager.RefreshSignInAsync(user);
            await RevokeAllTrustedDevicesForUser(user.Id);
            _logger.LogInformation("Two-factor authentication disabled for user {UserId}.", user.Id);
            TempData["SuccessMessage"] = "Two-factor authentication has been disabled.";
            return RedirectToAction(nameof(Profile));
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetAuthenticator()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            await _userManager.SetTwoFactorEnabledAsync(user, false);
            await _userManager.ResetAuthenticatorKeyAsync(user);
            await _userManager.UpdateSecurityStampAsync(user);
            await _signInManager.RefreshSignInAsync(user);
            await RevokeAllTrustedDevicesForUser(user.Id);
            _logger.LogInformation("Authenticator key reset for user {UserId}.", user.Id);
            TempData["SuccessMessage"] = "Your authenticator was reset. Set it up again to re-enable two-factor authentication.";
            return RedirectToAction(nameof(EnableTwoFactor));
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateRecoveryCodes()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            if (!user.TwoFactorEnabled)
            {
                TempData["ErrorMessage"] = "Enable two-factor authentication before generating recovery codes.";
                return RedirectToAction(nameof(EnableTwoFactor));
            }

            var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            return View("TwoFactorRecoveryCodes", new TwoFactorRecoveryCodesViewModel
            {
                RecoveryCodes = recoveryCodes?.ToList() ?? new List<string>()
            });
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> TrustedDevices()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            var devices = _trustedDevices is null
                ? new List<TrustedDevice>()
                : await _trustedDevices.GetActiveDevicesAsync(user.Id);

            var hasAuthenticator = !string.IsNullOrWhiteSpace(
                await _userManager.GetAuthenticatorKeyAsync(user));

            return View(new TrustedDevicesViewModel
            {
                Devices = devices,
                TwoFactorEnabled = user.TwoFactorEnabled,
                HasAuthenticator = hasAuthenticator,
                RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user)
            });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokeTrustedDevice(int deviceId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            if (_trustedDevices is not null)
            {
                await _trustedDevices.RevokeAsync(user.Id, deviceId);
            }

            TempData["SuccessMessage"] = "The trusted device was revoked.";
            return RedirectToAction(nameof(TrustedDevices));
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokeAllTrustedDevices()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return RedirectToAction(nameof(Login));
            }

            await RevokeAllTrustedDevicesForUser(user.Id);
            TempData["SuccessMessage"] = "All trusted devices were revoked.";
            return RedirectToAction(nameof(TrustedDevices));
        }

        private bool TryGetChallenge(string? challengeId, out string id, out TwoFactorChallenge challenge)
        {
            id = (challengeId ?? string.Empty).Trim();
            challenge = new TwoFactorChallenge();

            if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _))
            {
                return false;
            }

            if (!_cache.TryGetValue(ChallengeCacheKey(id), out TwoFactorChallenge? cached)
                || cached is null)
            {
                return false;
            }

            challenge = cached;
            return true;
        }

        private static string ChallengeCacheKey(string challengeId) =>
            TwoFactorAttemptCachePrefix + ":" + challengeId;

        private async Task<EnableTwoFactorViewModel> BuildEnableTwoFactorViewModel(
            ApplicationUser user, string? code = null)
        {
            var key = await _userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrWhiteSpace(key))
            {
                await _userManager.ResetAuthenticatorKeyAsync(user);
                key = await _userManager.GetAuthenticatorKeyAsync(user);
            }

            var issuer = "COMS";
            var account = user.Email ?? user.UserName ?? user.Id.ToString();
            var otpAuthUri =
                $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
                $"?secret={key}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

            return new EnableTwoFactorViewModel
            {
                SharedKey = FormatAuthenticatorKey(key),
                QrCodeUrl = BuildQrCodeDataUri(otpAuthUri),
                Code = code ?? string.Empty
            };
        }

        private static string NormalizeRecoveryCode(string code)
        {
            var compact = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            return compact.Length == 10
                ? compact[..5] + "-" + compact[5..]
                : compact;
        }

        private static string FormatAuthenticatorKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            return string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4)
                .Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
        }

        private static string BuildQrCodeDataUri(string otpAuthUri)
        {
            try
            {
                using var generator = new QRCoder.QRCodeGenerator();
                using var data = generator.CreateQrCode(otpAuthUri, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string BuildDeviceName()
        {
            var ua = Request.Headers.UserAgent.ToString();
            if (string.IsNullOrWhiteSpace(ua))
            {
                return "Web browser";
            }

            var os = ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
                : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
                : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
                : ua.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) ? "macOS"
                : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
                : "Unknown OS";

            var browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
                : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
                : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
                : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
                : "Browser";

            return browser + " on " + os;
        }

        private async Task RevokeAllTrustedDevicesForUser(int userId)
        {
            if (_trustedDevices is not null)
            {
                await _trustedDevices.RevokeAllAsync(userId);
            }

            TrustedDeviceCookie.Clear(HttpContext);
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
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Register(RegisterViewModel? model)
        {
            if (model is null)
            {
                ModelState.AddModelError(string.Empty, "Invalid registration request. Please try again.");
                ViewData["AvailableRoles"] = await GetAvailableRolesAsync(null);
                return View(new RegisterViewModel());
            }

            // Normalize input â€” leading/trailing spaces are a common failure point.
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
            // returns to the registration page â€” no pending row, no OTP.
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

                // Validate role against the self-registration allowlist â€” the same
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

                // NO ApplicationUser row is created here â€” Admin only ever sees
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

            // No User row exists yet at this stage by design â€” only a pending row.
            var pending = await FindPendingAsync(email.Trim());
            if (pending is null)
            {
                TempData["ErrorMessage"] = "No pending registration found for this email. Please register first.";
                return RedirectToAction(nameof(Register));
            }

            return View(new VerifyOtpViewModel { Email = pending.Email });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("auth")]
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
                if (await _userManager.FindByEmailAsync(pending.Email) is not null
                    || await _userManager.FindByNameAsync(pending.UserName) is not null)
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
        [EnableRateLimiting("auth")]
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
                TempData["ErrorMessage"] = "No pending registration found for this email. Please register first.";
                return RedirectToAction(nameof(Register));
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
        [EnableRateLimiting("auth")]
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
                // No crash: only in Development is the link written to logs so
                // local testing can continue without SMTP. In production the
                // log must never contain the token-bearing URL.
                _logger.LogError(ex, "ForgotPassword: SMTP send failed.");
                if (_env.IsDevelopment())
                {
                    _logger.LogWarning(
                        "SMTP Failed or Unconfigured. COPY THIS RESET LINK TO TEST: {ResetLink}",
                        resetLink);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForgotPassword: unexpected email error.");
                if (_env.IsDevelopment())
                {
                    _logger.LogWarning(
                        "SMTP Failed or Unconfigured. COPY THIS RESET LINK TO TEST: {ResetLink}",
                        resetLink);
                }
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
        [EnableRateLimiting("auth")]
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
                // Generic confirmation â€” do not reveal that the account is missing.
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
                // A password reset invalidates all remembered devices.
                await RevokeAllTrustedDevicesForUser(user.Id);
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

            ViewBag.TwoFactorEnabled = user.TwoFactorEnabled;
            ViewBag.HasAuthenticator = !string.IsNullOrWhiteSpace(
                await _userManager.GetAuthenticatorKeyAsync(user));
            ViewBag.RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user);

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

                // Password changes revoke every trusted-device token.
                await RevokeAllTrustedDevicesForUser(user.Id);
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
                          <p style="color:#555;font-size:13px;">If you did not request this, you can safely ignore this email â€” your account keeps its current address.</p>
                          <p>â€” Canal Obstruction Maintenance System</p>
                        </div>
                        """);
                    TempData["SuccessMessage"] = "Profile saved. A confirmation link was sent to your new email â€” it takes effect only after you confirm it.";
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
        /// dashes, dots, and parentheses only â€” digits (and a leading +) are
        /// never altered, so a valid number is never silently changed into a
        /// different number. "09â€¦" and "+63â€¦" forms are both kept as entered.
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
        /// by an Admin via Users/Edit â€” never via crafted registration POSTs.
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
