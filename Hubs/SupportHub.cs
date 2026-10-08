namespace COMS_MVC.Hubs
{
    /// <summary>
    /// Real-time customer-service chat. Groups are scoped per ticket
    /// (support-ticket-{id}); only the owning resident and staff
    /// (CustomerService/Admin) may join. Internal notes use the staff-only
    /// support-staff group and are never sent to ticket groups.
    /// </summary>
    [Authorize]
    public class SupportHub : Hub
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationService _notifications;

        public SupportHub(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            INotificationService notifications)
        {
            _context = context;
            _userManager = userManager;
            _notifications = notifications;
        }

        public static string TicketGroup(int ticketId) => $"support-ticket-{ticketId}";

        private const string StaffGroup = "support-staff";

        private bool IsStaff() =>
            Context.User?.IsInRole("CustomerService") == true ||
            Context.User?.IsInRole("Admin") == true;

        /// <summary>Join a ticket conversation after ownership/staff check.</summary>
        public async Task JoinTicket(int ticketId)
        {
            if (!int.TryParse(_userManager.GetUserId(Context.User!), out var userId))
            {
                throw new HubException("Sign-in required.");
            }

            var ticket = await _context.SupportTickets.FindAsync(ticketId);
            if (ticket == null)
            {
                throw new HubException("Conversation not found.");
            }

            var staff = IsStaff();
            if (!staff && ticket.UserId != userId)
            {
                // Same response as missing: do not reveal other users' tickets.
                throw new HubException("Conversation not found.");
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
            if (staff)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup);
            }
        }

        public async Task LeaveTicket(int ticketId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
        }

        /// <summary>Send a public chat message inside a ticket the user belongs to.</summary>
        public async Task SendMessage(int ticketId, string message)
        {
            if (!int.TryParse(_userManager.GetUserId(Context.User!), out var userId))
            {
                throw new HubException("Sign-in required.");
            }

            message = (message ?? string.Empty).Trim();
            if (message.Length == 0 || message.Length > 2000)
            {
                throw new HubException("Message must be 1-2000 characters.");
            }

            var ticket = await _context.SupportTickets.FindAsync(ticketId);
            if (ticket == null)
            {
                throw new HubException("Conversation not found.");
            }

            var staff = IsStaff();
            if (!staff && ticket.UserId != userId)
            {
                throw new HubException("Conversation not found.");
            }

            // Closed tickets are read-only for messaging: reopen via an
            // explicit status action first (resident Reopen / staff UpdateStatus).
            if (ticket.Status == "Closed")
            {
                throw new HubException("This ticket is closed. Reopen it before sending messages.");
            }

            var sender = await _userManager.FindByIdAsync(userId.ToString());
            var now = DateTime.UtcNow;

            var chat = new SupportMessage
            {
                SupportTicketId = ticketId,
                SenderUserId = userId,
                Message = message,
                SentAt = now,
                IsRead = false,
                IsInternal = false
            };
            _context.SupportMessages.Add(chat);

            ticket.UpdatedAt = now;
            await _context.SaveChangesAsync();

            await Clients.Group(TicketGroup(ticketId)).SendAsync("SupportMessageReceived", new
            {
                messageId = chat.SupportMessageId,
                ticketId,
                senderUserId = userId,
                senderName = sender?.FullName ?? sender?.UserName ?? "User",
                senderIsStaff = staff,
                text = message,
                sentAt = now.ToString("g")
            });

            // Notify the other side through the existing notification pipeline
            // (persists + pushes the MonitoringHub toast; best-effort).
            try
            {
                if (staff)
                {
                    await _notifications.CreateNotificationAsync(ticket.UserId,
                        "Customer Service replied",
                        $"Reply on '{ticket.Subject}'.",
                        "Support");
                }
                else
                {
                    await _notifications.CreateNotificationForRolesAsync(
                        new[] { "CustomerService", "Admin" },
                        "New support message",
                        $"Resident reply on ticket #{ticket.SupportTicketId}: '{ticket.Subject}'.",
                        "Support");
                }
            }
            catch
            {
                // Chat already saved; notifications are best-effort.
            }
        }

        /// <summary>Mark messages from the other side as read (own ticket only).</summary>
        public async Task MarkTicketRead(int ticketId)
        {
            if (!int.TryParse(_userManager.GetUserId(Context.User!), out var userId))
            {
                return;
            }

            var ticket = await _context.SupportTickets.FindAsync(ticketId);
            if (ticket == null)
            {
                return;
            }

            if (!IsStaff() && ticket.UserId != userId)
            {
                return;
            }

            // Residents never see internal notes, so never mark them here.
            var unread = await _context.SupportMessages
                .Where(m => m.SupportTicketId == ticketId
                    && !m.IsRead
                    && m.SenderUserId != userId
                    && (IsStaff() || !m.IsInternal))
                .ToListAsync();

            if (unread.Count == 0)
            {
                return;
            }

            foreach (var m in unread)
            {
                m.IsRead = true;
            }
            await _context.SaveChangesAsync();
        }
    }
}
