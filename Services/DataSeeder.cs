using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace COMS_MVC.Services
{
    public interface IDataSeeder
    {
        Task SeedAsync();
    }

    public class DataSeeder : IDataSeeder
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole<int>> _roleManager;
        private readonly ApplicationDbContext _db;
        private readonly ILogger<DataSeeder> _logger;

        public DataSeeder(UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole<int>> roleManager,
            ApplicationDbContext db,
            ILogger<DataSeeder> logger)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _db = db;
            _logger = logger;
        }

        public async Task SeedAsync()
        {
            await EnsureRolesAsync();
            await SeedUsersAsync();
            await SeedLookupOptionsAsync();

            // No demo canals/sensors — fresh install stays at 0
            // until the user adds input via the UI.
            _logger.LogInformation("Database seeding completed (roles + users only, no demo canals/sensors).");
        }

        private async Task EnsureRolesAsync()
        {
            var roles = new[] { "Admin", "LGU", "Barangay", "Maintenance", "Resident", "CustomerService" };
            foreach (var role in roles)
            {
                if (!await _roleManager.RoleExistsAsync(role))
                {
                    await _roleManager.CreateAsync(new IdentityRole<int> { Name = role, NormalizedName = role.ToUpper() });
                    _logger.LogInformation("Created role: {Role}", role);
                }
            }
        }

        /// <summary>
        /// Idempotent seed of dropdown options: inserts missing (Category,Value)
        /// rows only; repairs label/order on active rows. Never reactivates a
        /// deactivated option and never deletes anything — operator changes
        /// made directly in the database are preserved. Safe on every startup.
        /// </summary>
        public async Task SeedLookupOptionsAsync()
        {
            try
            {
                var existing = await _db.LookupOptions.ToListAsync();
                var changed = false;
                foreach (var kv in LookupService.SeedData)
                {
                    var order = 0;
                    foreach (var value in kv.Value)
                    {
                        var row = existing.FirstOrDefault(e => e.Category == kv.Key && e.Value == value);
                        var label = value switch
                        {
                            "LGU_Barangay" => "LGU & Barangay",
                            "Maintenance_Resident" => "Maintenance Personnel",
                            "All" when kv.Key == LookupService.Categories.AnnouncementAudience => "All Users",
                            _ => value
                        };
                        if (row == null)
                        {
                            _db.LookupOptions.Add(new LookupOption
                            {
                                Category = kv.Key, Value = value, DisplayName = label,
                                SortOrder = order, IsActive = true, CreatedAt = DateTime.UtcNow
                            });
                            changed = true;
                        }
                        else if (row.IsActive && (row.DisplayName != label || row.SortOrder != order))
                        {
                            row.DisplayName = label; row.SortOrder = order;
                            changed = true;
                        }
                        order++;
                    }
                }
                if (changed) await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Table may not exist yet when an old DB runs before migration.
                _logger.LogWarning(ex, "Lookup option seeding skipped.");
            }
        }

        private async Task SeedUsersAsync()
        {
            // NOTE: no Resident demo account is seeded. Residents self-register
            // through /Account/Register with OTP verification. If a
            // "resident@coms.gov" row already exists in a database, it is left
            // untouched: identify it by email/username, inspect its reports,
            // tickets, notifications and announcements, and only then decide
            // (with the data owner) whether to keep, reassign, or remove it
            // via the Admin Users interface. Never delete it automatically.
            var demoUsers = new[]
            {
                ("admin@coms.gov", "admin", "COMS Admin", "Admin", "Admin@COMS123!"),
                ("lgu@coms.gov", "lguuser", "LGU Officer", "LGU", "LGU@COMS123!"),
                ("barangay@coms.gov", "bguser", "Barangay Captain", "Barangay", "Barangay@COMS123!"),
                ("maintenance@coms.gov", "maintuser", "Maintenance Lead", "Maintenance", "Maintenance@COMS123!"),
            };

            foreach (var (email, userName, fullName, role, password) in demoUsers)
            {
                // Match by email first, then by username — either one means "already exists",
                // so we repair instead of creating a duplicate.
                var existing = await _userManager.FindByEmailAsync(email)
                    ?? await _userManager.FindByNameAsync(userName);

                if (existing == null)
                {
                    var user = new ApplicationUser
                    {
                        UserName = userName,
                        Email = email,
                        EmailConfirmed = true,
                        FullName = fullName,
                        PhoneNumber = "+639123456789",
                        Barangay = role == "Barangay" || role == "Resident" ? "Centro" : string.Empty,
                        City = role == "LGU" ? "Quezon City" : "Manila",
                        Address = "123 Demo Street",
                        CreatedAt = DateTime.UtcNow
                    };

                    var result = await _userManager.CreateAsync(user, password);
                    if (result.Succeeded)
                    {
                        await _userManager.AddToRoleAsync(user, role);
                        _logger.LogInformation("Created user: {Email} in role {Role}", email, role);
                    }
                    else
                    {
                        _logger.LogError("Failed to create user {Email}: {Errors}",
                            email, string.Join("; ", result.Errors.Select(e => e.Description)));
                    }
                }
                else
                {
                    // Repair path: fix username/email drift, confirm email, clear lockout,
                    // reset to the known demo password, and ensure the correct role.
                    // This is what restores e.g. a deleted-or-corrupted LGU account
                    // without ever creating a second row.
                    var nameResult = await _userManager.SetUserNameAsync(existing, userName);
                    if (!nameResult.Succeeded)
                    {
                        _logger.LogError("Failed to set username for {Email}: {Errors}",
                            email, string.Join("; ", nameResult.Errors.Select(e => e.Description)));
                        continue;
                    }

                    var emailResult = await _userManager.SetEmailAsync(existing, email);
                    if (!emailResult.Succeeded)
                    {
                        _logger.LogError("Failed to set email for {UserName}: {Errors}",
                            userName, string.Join("; ", emailResult.Errors.Select(e => e.Description)));
                        continue;
                    }

                    existing.FullName = fullName;
                    existing.EmailConfirmed = true;
                    await _userManager.UpdateAsync(existing);

                    // Clear any lockout so a previously locked demo account can sign in.
                    await _userManager.SetLockoutEndDateAsync(existing, null);
                    await _userManager.ResetAccessFailedCountAsync(existing);

                    var token = await _userManager.GeneratePasswordResetTokenAsync(existing);
                    var pwdResult = await _userManager.ResetPasswordAsync(existing, token, password);
                    if (!pwdResult.Succeeded)
                    {
                        _logger.LogError("Failed to reset password for {Email}: {Errors}",
                            email, string.Join("; ", pwdResult.Errors.Select(e => e.Description)));
                        continue;
                    }

                    if (!await _userManager.IsInRoleAsync(existing, role))
                    {
                        var currentRoles = await _userManager.GetRolesAsync(existing);
                        if (currentRoles.Count > 0)
                        {
                            await _userManager.RemoveFromRolesAsync(existing, currentRoles);
                        }
                        await _userManager.AddToRoleAsync(existing, role);
                    }

                    _logger.LogInformation("Repaired user: {Email} in role {Role}", email, role);
                }
            }
        }
    }
}
