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
        private readonly ILogger<DataSeeder> _logger;

        public DataSeeder(UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole<int>> roleManager,
            ILogger<DataSeeder> logger)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _logger = logger;
        }

        public async Task SeedAsync()
        {
            await EnsureRolesAsync();
            await SeedUsersAsync();

            // No demo canals/sensors — fresh install stays at 0
            // until the user adds input via the UI.
            _logger.LogInformation("Database seeding completed (roles + users only, no demo canals/sensors).");
        }

        private async Task EnsureRolesAsync()
        {
            var roles = new[] { "Admin", "LGU", "Barangay", "Maintenance", "Resident" };
            foreach (var role in roles)
            {
                if (!await _roleManager.RoleExistsAsync(role))
                {
                    await _roleManager.CreateAsync(new IdentityRole<int> { Name = role, NormalizedName = role.ToUpper() });
                    _logger.LogInformation("Created role: {Role}", role);
                }
            }
        }

        private async Task SeedUsersAsync()
        {
            var demoUsers = new[]
            {
                ("admin@coms.gov", "admin", "COMS Admin", "Admin", "Admin@COMS123!"),
                ("lgu@coms.gov", "lguuser", "LGU Officer", "LGU", "LGU@COMS123!"),
                ("barangay@coms.gov", "bguser", "Barangay Captain", "Barangay", "Barangay@COMS123!"),
                ("maintenance@coms.gov", "maintuser", "Maintenance Lead", "Maintenance", "Maintenance@COMS123!"),
                ("resident@coms.gov", "resident", "Juan Dela Cruz", "Resident", "Resident@COMS123!")
            };

            foreach (var (email, userName, fullName, role, password) in demoUsers)
            {
                var existing = await _userManager.FindByEmailAsync(email);

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
                }
                else
                {
                    existing.UserName = userName;
                    existing.Email = email;
                    existing.FullName = fullName;
                    existing.EmailConfirmed = true;
                    await _userManager.UpdateAsync(existing);

                    await _userManager.RemovePasswordAsync(existing);
                    await _userManager.AddPasswordAsync(existing, password);
                    _logger.LogInformation("Reset password for user: {Email} in role {Role}", email, role);
                }
            }
        }
    }
}
