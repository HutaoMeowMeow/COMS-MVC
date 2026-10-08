using Microsoft.AspNetCore.Identity;

namespace COMS_MVC.Controllers
{
    [Authorize(Roles = "Admin")]
    public class UsersController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole<int>> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<UsersController> _logger;

        private static readonly List<string> AllRoles = new() { "Admin", "LGU", "Barangay", "Maintenance", "Resident", "CustomerService" };

        public UsersController(UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole<int>> roleManager,
            ApplicationDbContext context,
            ILogger<UsersController> logger)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var users = await _userManager.Users.ToListAsync();

            var userRoles = new List<UserRoleViewModel>();
            foreach (var user in users)
            {
                var roles = await _userManager.GetRolesAsync(user);
                userRoles.Add(new UserRoleViewModel
                {
                    Id = user.Id,
                    UserName = user.UserName,
                    FullName = user.FullName,
                    Email = user.Email,
                    Roles = roles.ToList(),
                    CreatedAt = user.CreatedAt
                });
            }

            return View(userRoles);
        }

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
                AllRoles = AllRoles
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
                AllRoles = AllRoles
            };

            ViewBag.IsOwnAccount = user.UserName == User?.Identity?.Name;
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
                ViewBag.IsOwnAccount = model.UserName == User?.Identity?.Name;
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
            model.SelectedRoles = model.SelectedRoles.Where(r => AllRoles.Contains(r)).Distinct().ToList();
            if (model.SelectedRoles.Count == 0)
            {
                ModelState.AddModelError(nameof(model.SelectedRoles), "Please select at least one valid role.");
            }
            var isOwnAccount = user.UserName == User?.Identity?.Name;
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
                    await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
                }

                if (rolesToAdd.Any())
                {
                    await _userManager.AddToRolesAsync(user, rolesToAdd);
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

            if (user.UserName == User?.Identity?.Name)
            {
                TempData["ErrorMessage"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Index));
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
                CreatedAt = user.CreatedAt,
                Roles = roles.ToList(),
                AllRoles = AllRoles
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

            if (user.UserName == User?.Identity?.Name)
            {
                TempData["ErrorMessage"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                _logger.LogInformation("User deleted: {UserName}", user.UserName);
                TempData["SuccessMessage"] = $"User '{user.UserName}' deleted successfully.";
            }
            else
            {
                TempData["ErrorMessage"] = "Failed to delete user.";
            }

            return RedirectToAction(nameof(Index));
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
                TempData["SuccessMessage"] = $"Password for '{user.UserName}' has been reset successfully.";
                return RedirectToAction(nameof(Details), new { id = user.Id });
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }
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
        [StringLength(100, ErrorMessage = "Full name cannot exceed 100 characters")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters")]
        public string Email { get; set; } = string.Empty;

        [Phone(ErrorMessage = "Invalid phone number format")]
        [StringLength(30, ErrorMessage = "Phone number cannot exceed 30 characters")]
        public string? PhoneNumber { get; set; }

        [StringLength(100, ErrorMessage = "Barangay cannot exceed 100 characters")]
        public string? Barangay { get; set; }

        [StringLength(100, ErrorMessage = "City cannot exceed 100 characters")]
        public string? City { get; set; }

        [StringLength(250, ErrorMessage = "Address cannot exceed 250 characters")]
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
