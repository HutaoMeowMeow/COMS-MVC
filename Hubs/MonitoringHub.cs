using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace COMS_MVC.Hubs
{
    [Authorize]
    public class MonitoringHub : Hub
    {
        /// <summary>
        /// Staff roles entitled to live sensor/alert/risk broadcasts.
        /// Residents and role-less accounts are excluded: they are also
        /// excluded from the corresponding list pages.
        /// </summary>
        public static readonly IReadOnlyList<string> StaffGroups = new[]
        {
            "role_Admin", "role_LGU", "role_Barangay", "role_Maintenance"
        };

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public MonitoringHub(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }
        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var roles = new[] { "Admin", "LGU", "Barangay", "Maintenance", "Resident", "CustomerService" }
                .Where(r => Context.User?.IsInRole(r) == true)
                .ToList();
            var role = roles.FirstOrDefault() ?? "Anonymous";

            if (!string.IsNullOrEmpty(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
                foreach (var r in roles)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"role_{r}");
                }
                if (roles.Count == 0)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, "role_Anonymous");
                }
            }

            await Clients.Caller.SendAsync("Connected", new
            {
                UserId = userId,
                Role = role,
                ConnectionId = Context.ConnectionId
            });

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task JoinRoleGroup(string roleName)
        {
            var userRole = Context.User?.IsInRole(roleName) == true;
            if (userRole)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"role_{roleName}");
                await Clients.Caller.SendAsync("JoinedGroup", $"role_{roleName}");
            }
        }

        public async Task LeaveRoleGroup(string roleName)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"role_{roleName}");
            await Clients.Caller.SendAsync("LeftGroup", $"role_{roleName}");
        }

        public async Task JoinCanalGroup(int canalId)
        {
            if (canalId <= 0)
            {
                return;
            }
            // Residents have no canal access; only staff roles may subscribe
            // to per-canal updates.
            if (Context.User?.IsInRole("Resident") == true
                && Context.User?.IsInRole("Admin") != true
                && Context.User?.IsInRole("LGU") != true
                && Context.User?.IsInRole("Barangay") != true
                && Context.User?.IsInRole("Maintenance") != true)
            {
                return;
            }
            // Barangay users may only subscribe to their own barangay's canals.
            if (Context.User?.IsInRole("Barangay") == true
                && Context.User?.IsInRole("Admin") != true
                && Context.User?.IsInRole("LGU") != true)
            {
                var user = await _userManager.GetUserAsync(Context.User!);
                var canal = await _context.Canals
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CanalId == canalId);
                if (canal == null || canal.Barangay != user?.Barangay)
                {
                    return;
                }
            }
            await Groups.AddToGroupAsync(Context.ConnectionId, $"canal_{canalId}");
            await Clients.Caller.SendAsync("JoinedCanal", canalId);
        }
    }
}
