using COMS_MVC.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize(Roles = "Admin")]
    public class UsersController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole<int>> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly IOtpService _otpService;
        private readonly ILogger<UsersController> _logger;
        private readonly ITrustedDeviceService? _trustedDevices;

        public UsersController(UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole<int>> roleManager,
            ApplicationDbContext context,
            IOtpService otpService,
            ILogger<UsersController> logger,
            ITrustedDeviceService? trustedDevices = null)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _otpService = otpService;
            _logger = logger;
            _trustedDevices = trustedDevices;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // Batched role lookup: 2 queries total instead of one per user (N+1).
            var users = await _userManager.Users.ToListAsync();
            var userIds = users.Select(u => u.Id).ToList();
            var userRolePairs = await _context.Set<IdentityUserRole<int>>()
                .AsNoTracking()
                .Where(ur => userIds.Contains(ur.UserId))
                .ToListAsync();
            var roleNamesById = await _roleManager.Roles
                .AsNoTracking()
                .ToDictionaryAsync(r => r.Id, r => r.Name);

            var rolesByUserId = userRolePairs
                .GroupBy(ur => ur.UserId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(ur => roleNamesById.TryGetValue(ur.RoleId, out var name) ? name ?? string.Empty : string.Empty)
                        .Where(n => n.Length > 0)
                        .ToList());

            var userRoles = new List<UserRoleViewModel>();
            foreach (var user in users)
            {
                rolesByUserId.TryGetValue(user.Id, out var roles);
                userRoles.Add(new UserRoleViewModel
                {
                    Id = user.Id,
                    UserName = user.UserName,
                    FullName = user.FullName,
                    Email = user.Email,
                    Roles = roles ?? new List<string>(),
                    CreatedAt = user.CreatedAt
                });
            }

            return View(userRoles);
        }

        /// <summary>
        /// Admin creates a staff/field account (e.g. Maintenance) through the
        /// existing user-management interface. The account is NOT created
        /// directly: a pending registration + OTP email go through the same
        /// verification workflow as self-registration, so email ownership is
        /// always proven. The Admin role can never be granted here.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            ViewBag.CreateRoles = new SelectList(await GetCreatableRoleNamesAsync(), "Maintenance");
            return View(new RegisterViewModel { Role = "Maintenance" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(RegisterViewModel? model)
        {
            ViewBag.CreateRoles = new SelectList(await GetCreatableRoleNamesAsync(), model?.Role);
            if (model is null)
            {
                ModelState.AddModelError(string.Empty, "Invalid request. Please try again.");
                return View(new RegisterViewModel { Role = "Maintenance" });
            }

            model.FullName = (model.FullName ?? string.Empty).Trim();
            model.UserName = (model.UserName ?? string.Empty).Trim();
            model.Email = (model.Email ?? string.Empty).Trim();
            model.PhoneNumber = AccountController.NormalizePhone(model.PhoneNumber);
            model.Barangay = string.IsNullOrWhiteSpace(model.Barangay) ? null : model.Barangay.Trim();
            model.City = string.IsNullOrWhiteSpace(model.City) ? null : model.City.Trim();
            model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
            model.Role = string.IsNullOrWhiteSpace(model.Role) ? "Maintenance" : model.Role.Trim();

            ModelState.Clear();
            var normalizedResults = new List<ValidationResult>();
            Validator.TryValidateObject(
                model, new ValidationContext(model), normalizedResults, validateAllProperties: true);
            foreach (var vr in normalizedResults)
            {
                foreach (var name in vr.MemberNames.DefaultIfEmpty(string.Empty))
                {
                    ModelState.AddModelError(name, vr.ErrorMessage ?? "Invalid value.");
                }
            }
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // Role comes from the form but is never trusted: it must exist
            // and must not be Admin.
            var allowedRoles = await GetCreatableRoleNamesAsync();
            if (!allowedRoles.Contains(model.Role))
            {
                _logger.LogWarning("Admin attempted to create account with disallowed role '{Role}'.", model.Role);
                ModelState.AddModelError(nameof(model.Role), "Please select a valid role.");
                return View(model);
            }
            var role = model.Role;

            if (await _userManager.FindByNameAsync(model.UserName) is not null)
            {
                ModelState.AddModelError(nameof(model.UserName), "Username is already taken.");
                return View(model);
            }
            if (await _userManager.FindByEmailAsync(model.Email) is not null)
            {
                ModelState.AddModelError(nameof(model.Email), "Email is already registered.");
                return View(model);
            }

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

            try
            {
                var normalizedEmail = _userManager.NormalizeEmail(model.Email);
                var normalizedUserName = _userManager.NormalizeName(model.UserName);
                var nameClash = await _context.PendingRegistrations
                    .FirstOrDefaultAsync(p => p.NormalizedUserName == normalizedUserName
                        && p.NormalizedEmail != normalizedEmail);
                if (nameClash is not null)
                {
                    ModelState.AddModelError(nameof(model.UserName), "Username is already taken.");
                    return View(model);
                }
                var pending = await _context.PendingRegistrations
                    .FirstOrDefaultAsync(p => p.NormalizedEmail == normalizedEmail);
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
                    _context.PendingRegistrations.Add(pending);
                }
                pending.UserName = model.UserName;
                pending.NormalizedUserName = normalizedUserName;
                pending.FullName = model.FullName;
                pending.PhoneNumber = model.PhoneNumber;
                pending.Barangay = model.Barangay;
                pending.City = model.City;
                pending.Address = model.Address;
                pending.Role = role;
                pending.PasswordHash = _userManager.PasswordHasher.HashPassword(probe, model.Password ?? string.Empty);

                var otp = await _otpService.IssueAsync(pending);
                await _context.SaveChangesAsync();

                if (!otp.Succeeded)
                {
                    TempData["ErrorMessage"] = $"Account invitation saved, but the verification email could not be sent: {otp.Error}";
                }
                else
                {
                    TempData["SuccessMessage"] = $"Invitation sent to {pending.Email}. They must enter the verification code to activate the {role} account.";
                }
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating account invitation for: {Email}", model.Email);
                ModelState.AddModelError(string.Empty, "Could not create the account. Please try again.");
                return View(model);
            }
        }

        /// <summary>
        /// Roles an Admin may grant at creation. Admin is intentionally
        /// excluded: new Admins come only from deployment-time seeding.
        /// </summary>
        private async Task<List<string>> GetCreatableRoleNamesAsync() =>
            (await GetAllRoleNamesAsync()).Where(r => r != "Admin").OrderBy(n => n).ToList();

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null)
            {
                return NotFound();
            }

            var roles = await _userManager.GetRolesAsync(user);

            var model = new UserDetailsViewModel
            {
                Id = user.Id,
                UserName = user.UserName,
                FullName = user.FullName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                Barangay = user.Barangay,
                City = user.City,
                Address = user.Address,
                CreatedAt = user.CreatedAt,
                Roles = roles.ToList(),
                AllRoles = await GetAllRoleNamesAsync()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null)
            {
                return NotFound();
            }

            var roles = await _userManager.GetRolesAsync(user);

            var model = new UserEditViewModel
            {
                Id = user.Id,
                UserName = user.UserName,
                FullName = user.FullName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                Barangay = user.Barangay,
                City = user.City,
                Address = user.Address,
                CreatedAt = user.CreatedAt,
                SelectedRoles = roles.ToList(),
                AllRoles = await GetAllRoleNamesAsync()
            };

            ViewBag.IsOwnAccount = user.Id.ToString() == _userManager.GetUserId(User);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, UserEditViewModel model)
        {
            if (id != model.Id)
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                ViewBag.IsOwnAccount = model.Id.ToString() == _userManager.GetUserId(User);
                return View(model);
            }

            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null)
            {
                return NotFound();
            }

            model.FullName = (model.FullName ?? string.Empty).Trim();
            model.Email = (model.Email ?? string.Empty).Trim();
            model.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber.Trim();
            model.Barangay = string.IsNullOrWhiteSpace(model.Barangay) ? null : model.Barangay.Trim();
            model.City = string.IsNullOrWhiteSpace(model.City) ? null : model.City.Trim();
            model.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();
            model.SelectedRoles ??= new List<string>();
            var validRoles = await GetAllRoleNamesAsync();
            model.SelectedRoles = model.SelectedRoles.Where(r => validRoles.Contains(r)).Distinct().ToList();
            if (model.SelectedRoles.Count == 0)
            {
                ModelState.AddModelError(nameof(model.SelectedRoles), "Please select at least one valid role.");
            }
            var isOwnAccount = user.Id.ToString() == _userManager.GetUserId(User);
            if (isOwnAccount && !model.SelectedRoles.Contains("Admin") && (await _userManager.GetRolesAsync(user)).Contains("Admin"))
            {
                ModelState.AddModelError(nameof(model.SelectedRoles), "You cannot remove the Admin role from your own account.");
            }

            if (!ModelState.IsValid)
            {
                ViewBag.IsOwnAccount = isOwnAccount;
                return View(model);
            }

            var emailOwner = await _userManager.FindByEmailAsync(model.Email);
            if (emailOwner != null && emailOwner.Id != user.Id)
            {
                ModelState.AddModelError(nameof(model.Email), "This email is already registered to another account.");
                ViewBag.IsOwnAccount = isOwnAccount;
                return View(model);
            }

            user.FullName = model.FullName;
            user.Email = model.Email;
            user.UserName = user.UserName;
            user.PhoneNumber = model.PhoneNumber;
            user.Barangay = model.Barangay;
            user.City = model.City;
            user.Address = model.Address;

            IdentityResult result;
            try
            {
                result = await _userManager.UpdateAsync(user);
            }
            catch (Exception)
            {
                ModelState.AddModelError(string.Empty, "Could not save the user. Please try again.");
                ViewBag.IsOwnAccount = isOwnAccount;
                return View(model);
            }
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
                ViewBag.IsOwnAccount = isOwnAccount;
                return View(model);
            }

            try
            {
                var currentRoles = await _userManager.GetRolesAsync(user);

                var rolesToRemove = currentRoles.Except(model.SelectedRoles).ToList();
                var rolesToAdd = model.SelectedRoles.Except(currentRoles).ToList();

                if (rolesToRemove.Any())
                {
                    var removeResult = await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
                    if (!removeResult.Succeeded)
                    {
                        foreach (var error in removeResult.Errors)
                        {
                            ModelState.AddModelError(string.Empty, error.Description);
                        }
                        ViewBag.IsOwnAccount = user.Id.ToString() == _userManager.GetUserId(User);
                        return View(model);
                    }
                }

                if (rolesToAdd.Any())
                {
                    var addResult = await _userManager.AddToRolesAsync(user, rolesToAdd);
                    if (!addResult.Succeeded)
                    {
                        foreach (var error in addResult.Errors)
                        {
                            ModelState.AddModelError(string.Empty, error.Description);
                        }
                        ViewBag.IsOwnAccount = user.Id.ToString() == _userManager.GetUserId(User);
                        return View(model);
                    }
                }

                if (rolesToRemove.Any() || rolesToAdd.Any())
                {
                    // New stamp invalidates the user's existing sessions and
                    // SignalR connections at the next validation check, so a
                    // removed role stops working promptly.
                    await _userManager.UpdateSecurityStampAsync(user);
                }
            }
            catch (Exception)
            {
                TempData["ErrorMessage"] = "User details saved, but roles could not be fully updated. Please review the roles.";
                return RedirectToAction(nameof(Details), new { id = user.Id });
            }

            TempData["SuccessMessage"] = "User updated successfully.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Delete(int id)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null)
            {
                return NotFound();
            }

            if (user.Id.ToString() == _userManager.GetUserId(User))
            {
                TempData["ErrorMessage"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Index));
            }

            var roles = await _userManager.GetRolesAsync(user);

            // Show the admin upfront which related records would block deletion,
            // so the confirmation page reports the real outcome in advance.
            ViewBag.Blockers = await GetDeletionBlockersAsync(id);

            var model = new UserDetailsViewModel
            {
                Id = user.Id,
                UserName = user.UserName,
                FullName = user.FullName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                Barangay = user.Barangay,
                City = user.City,
                CreatedAt = user.CreatedAt,
                Roles = roles.ToList(),
                AllRoles = await GetAllRoleNamesAsync()
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id, IFormCollection collection)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null)
            {
                return NotFound();
            }

            if (user.Id.ToString() == _userManager.GetUserId(User))
            {
                TempData["ErrorMessage"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Index));
            }

            // Historical records (reports, tickets, messages, announcements)
            // use Restrict FKs and must be preserved: refuse with a specific
            // message naming what blocks deletion, instead of failing on a
            // raw database error.
            var blockers = await GetDeletionBlockersAsync(id);
            if (blockers.Count > 0)
            {
                TempData["ErrorMessage"] =
                    $"User '{user.UserName}' cannot be deleted while they still own: " +
                    string.Join(", ", blockers.Select(b => $"{b.Count} {b.Label}")) +
                    ". Delete or reassign those records first (Reports / Customer Service / Announcements).";
                return RedirectToAction(nameof(Index));
            }

            // The user's notification inbox is transient per-account data with
            // no value once the account is gone (nothing references it), so it
            // is removed atomically with the account. Everything else that
            // blocks has already been refused above.
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                var inbox = await _context.Notifications
                    .Where(n => n.UserId == id)
                    .ToListAsync();
                if (inbox.Count > 0)
                {
                    _context.Notifications.RemoveRange(inbox);
                    await _context.SaveChangesAsync();
                }

                var result = await _userManager.DeleteAsync(user);
                if (!result.Succeeded)
                {
                    await tx.RollbackAsync();
                    TempData["ErrorMessage"] =
                        "Failed to delete user: " +
                        string.Join("; ", result.Errors.Select(e => e.Description));
                    return RedirectToAction(nameof(Index));
                }

                await tx.CommitAsync();
                _logger.LogInformation("User deleted: {UserName}", user.UserName);
                TempData["SuccessMessage"] = $"User '{user.UserName}' deleted successfully.";
            }
            catch (DbUpdateException ex) when (IsForeignKeyError(ex))
            {
                await tx.RollbackAsync();
                TempData["ErrorMessage"] = "This user owns records (reports, tickets, announcements, or notifications) and cannot be deleted while they exist. Reassign or delete those records first.";
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError(ex, "Unexpected error deleting user {UserName}.", user.UserName);
                TempData["ErrorMessage"] = "An unexpected error occurred while deleting the user. Please try again.";
            }

            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Counts of the user's historical records that block deletion under
        /// the Restrict FKs. Notification-inbox rows are excluded: they are
        /// removed atomically with the account and never block it.
        /// </summary>
        private async Task<List<(string Label, int Count)>> GetDeletionBlockersAsync(int userId)
        {
            var blockers = new List<(string Label, int Count)>();
            var reports = await _context.CommunityReports.CountAsync(r => r.UserId == userId);
            if (reports > 0) blockers.Add(("community report(s)", reports));
            var tickets = await _context.SupportTickets.CountAsync(t => t.UserId == userId);
            if (tickets > 0) blockers.Add(("support ticket(s)", tickets));
            var messages = await _context.SupportMessages.CountAsync(m => m.SenderUserId == userId);
            if (messages > 0) blockers.Add(("support message(s)", messages));
            var announcements = await _context.Announcements.CountAsync(a => a.PostedByUserId == userId);
            if (announcements > 0) blockers.Add(("announcement(s)", announcements));
            return blockers;
        }

        private static bool IsForeignKeyError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var msg = e.Message ?? string.Empty;
                if (msg.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("foreign key", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("23503")
                    || msg.Contains("FK_", StringComparison.OrdinalIgnoreCase)
                    || e.GetType().Name.Contains("DbUpdateException"))
                {
                    return true;
                }
            }
            return false;
        }

        [HttpGet]
        public async Task<IActionResult> ResetPassword(int id)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null)
            {
                return NotFound();
            }

            var model = new ResetPasswordViewModel
            {
                UserId = user.Id,
                UserName = user.UserName,
                Email = user.Email
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetTwoFactor(int id)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null)
            {
                return NotFound();
            }

            await _userManager.SetTwoFactorEnabledAsync(user, false);
            await _userManager.ResetAuthenticatorKeyAsync(user);
            await _userManager.UpdateSecurityStampAsync(user);
            if (_trustedDevices is not null)
            {
                await _trustedDevices.RevokeAllAsync(user.Id);
            }

            _logger.LogInformation("Administrator reset two-factor authentication for user {UserId}.", user.Id);
            TempData["SuccessMessage"] = $"Two-factor authentication for '{user.UserName}' has been reset.";
            return RedirectToAction(nameof(Details), new { id = user.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var user = await _userManager.FindByIdAsync(model.UserId.ToString());
            if (user == null)
            {
                return NotFound();
            }

            IdentityResult result;
            try
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                result = await _userManager.ResetPasswordAsync(user, token, model.NewPassword);
            }
            catch (Exception)
            {
                ModelState.AddModelError(string.Empty, "Could not reset the password. Please try again.");
                return View(model);
            }

            if (result.Succeeded)
            {
                // New stamp signs out the user's other sessions promptly.
                await _userManager.UpdateSecurityStampAsync(user);
                if (_trustedDevices is not null)
                {
                    await _trustedDevices.RevokeAllAsync(user.Id);
                }
                TempData["SuccessMessage"] = $"Password for '{user.UserName}' has been reset successfully.";
                return RedirectToAction(nameof(Details), new { id = user.Id });
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        /// <summary>
        /// Role choices come from the AspNetRoles table, never a hardcoded list.
        /// </summary>
        private async Task<List<string>> GetAllRoleNamesAsync() =>
            await _roleManager.Roles
                .Where(r => r.Name != null)
                .Select(r => r.Name!)
                .OrderBy(n => n)
                .ToListAsync();
    }

    public class UserDetailsViewModel
    {
        public int Id { get; set; }

        public string UserName { get; set; }

        public string FullName { get; set; }

        public string Email { get; set; }

        public string PhoneNumber { get; set; }

        public string? Barangay { get; set; }

        public string? City { get; set; }

        public string? Address { get; set; }

        public DateTime CreatedAt { get; set; }

        public List<string> Roles { get; set; } = new List<string>();

        public List<string> AllRoles { get; set; } = new List<string>();
    }

    public class UserEditViewModel
    {
        public int Id { get; set; }

        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Full name is required")]
        [StringLength(InputLimits.FullName, ErrorMessage = "Full name cannot exceed 40 characters")]
        [RegularExpression(RegistrationPatterns.NameOrPlace,
            ErrorMessage = "Full name contains unsupported characters.")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters")]
        public string Email { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Invalid phone number format")]
        [StringLength(30, ErrorMessage = "Phone number cannot exceed 30 characters")]
        public string? PhoneNumber { get; set; }

        [StringLength(100, ErrorMessage = "Barangay cannot exceed 100 characters")]
        [RegularExpression(RegistrationPatterns.NameOrPlace,
            ErrorMessage = "Barangay contains unsupported characters.")]
        public string? Barangay { get; set; }

        [StringLength(100, ErrorMessage = "City cannot exceed 100 characters")]
        [RegularExpression(RegistrationPatterns.NameOrPlace,
            ErrorMessage = "City / Municipality contains unsupported characters.")]
        public string? City { get; set; }

        [StringLength(250, ErrorMessage = "Address cannot exceed 250 characters")]
        [RegularExpression(RegistrationPatterns.Address,
            ErrorMessage = "Address contains unsupported characters.")]
        public string? Address { get; set; }

        public DateTime CreatedAt { get; set; }

        public List<string> SelectedRoles { get; set; } = new List<string>();

        public List<string> AllRoles { get; set; } = new List<string>();
    }

    public class ResetPasswordViewModel
    {
        public int UserId { get; set; }

        public string UserName { get; set; }

        public string? Email { get; set; }

        [Required(ErrorMessage = "New password is required")]
        [StringLength(100, ErrorMessage = "Password must be at least 6 characters", MinimumLength = 6)]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; }

        [DataType(DataType.Password)]
        [Compare("NewPassword", ErrorMessage = "Passwords do not match")]
        public string ConfirmPassword { get; set; }
    }

    public class UserRoleViewModel
    {
        public int Id { get; set; }

        public string UserName { get; set; }

        public string FullName { get; set; }

        public string Email { get; set; }

        public List<string> Roles { get; set; } = new List<string>();

        public DateTime CreatedAt { get; set; }
    }
}
