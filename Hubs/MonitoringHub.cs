using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace COMS_MVC.Hubs
{
    [Authorize]
    public class MonitoringHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var role = Context.User?.IsInRole("Admin") == true ? "Admin"
                : Context.User?.IsInRole("LGU") == true ? "LGU"
                : Context.User?.IsInRole("Barangay") == true ? "Barangay"
                : Context.User?.IsInRole("Maintenance") == true ? "Maintenance"
                : Context.User?.IsInRole("Resident") == true ? "Resident"
                : "Anonymous";

            if (!string.IsNullOrEmpty(userId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
                await Groups.AddToGroupAsync(Context.ConnectionId, $"role_{role}");
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
            await Groups.AddToGroupAsync(Context.ConnectionId, $"canal_{canalId}");
            await Clients.Caller.SendAsync("JoinedCanal", canalId);
        }
    }
}
