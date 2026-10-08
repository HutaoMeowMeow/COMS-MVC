using Microsoft.AspNetCore.Mvc.Rendering;

namespace COMS_MVC.Controllers
{
    [Authorize]
    public class CustomerServiceController : Controller
    {
        private const string StaffRoles = "CustomerService,Admin";

        private static readonly List<string> Categories = new()
        {
            "Account", "Report", "Technical Issue", "General Inquiry", "Other"
        };

        private static readonly List<string> Priorities = new()
        {
            "Low", "Normal", "High", "Urgent"
        };

        private static readonly List<string> Statuses = new()
        {
            "Open", "In Progress", "Waiting for User", "Resolved", "Closed"
        };

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationService _notifications;
        private readonly IHubContext<Hubs.SupportHub> _supportHub;

        public CustomerServiceController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            INotificationService notifications,
            IHubContext<Hubs.SupportHub> supportHub)
        {
            _context = context;
            _userManager = userManager;
            _notifications = notifications;
            _supportHub = supportHub;
        }

        private bool IsStaff() => User.IsInRole("CustomerService") || User.IsInRole("Admin");

        private bool TryGetUserId(out int userId) =>
            int.TryParse(_userManager.GetUserId(User), out userId);

        // ================= Resident: Help Center =================

        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Index()
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var mine = await _context.SupportTickets
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.UpdatedAt)
                .Take(5)
                .ToListAsync();

            var ticketIds = mine.Select(t => t.SupportTicketId).ToList();
            ViewBag.UnreadByTicket = await _context.SupportMessages
                .Where(m => ticketIds.Contains(m.SupportTicketId)
                    && !m.IsInternal && !m.IsRead && m.SenderUserId != userId)
                .GroupBy(m => m.SupportTicketId)
                .ToDictionaryAsync(g => g.Key, g => g.Count());
            ViewBag.TotalUnread = await GetResidentUnreadCountAsync(userId);

            return View(mine);
        }

        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> MyTickets()
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var tickets = await _context.SupportTickets
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.UpdatedAt)
                .ToListAsync();

            var ticketIds = tickets.Select(t => t.SupportTicketId).ToList();
            ViewBag.UnreadByTicket = await _context.SupportMessages
                .Where(m => ticketIds.Contains(m.SupportTicketId)
                    && !m.IsInternal && !m.IsRead && m.SenderUserId != userId)
                .GroupBy(m => m.SupportTicketId)
                .ToDictionaryAsync(g => g.Key, g => g.Count());

            return View(tickets);
        }

        [HttpGet]
        [Authorize(Roles = "Resident")]
        public IActionResult Create()
        {
            ViewBag.Categories = new SelectList(Categories);
            ViewBag.Priorities = new SelectList(Priorities, "Normal");
            return View(new CreateTicketViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Create(CreateTicketViewModel model)
        {
            model.Subject = (model.Subject ?? string.Empty).Trim();
            model.Description = (model.Description ?? string.Empty).Trim();
            model.Category = (model.Category ?? string.Empty).Trim();
            model.Priority = (model.Priority ?? string.Empty).Trim();

            if (!Categories.Contains(model.Category))
            {
                ModelState.AddModelError(nameof(model.Category), "Please select a valid category.");
            }
            if (!Priorities.Contains(model.Priority))
            {
                ModelState.AddModelError(nameof(model.Priority), "Please select a valid priority.");
            }

            ViewBag.Categories = new SelectList(Categories, model.Category);
            ViewBag.Priorities = new SelectList(Priorities, model.Priority);

            if (!ModelState.IsValid)
            {
                return View(model);
            }
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var now = DateTime.UtcNow;
            var ticket = new SupportTicket
            {
                UserId = userId, // Server-side identity only. Never from input.
                Subject = model.Subject,
                Description = model.Description,
                Category = model.Category,
                Priority = model.Priority,
                Status = "Open",
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.SupportTickets.Add(ticket);
            await _context.SaveChangesAsync();

            try
            {
                await _notifications.CreateNotificationForRolesAsync(
                    new[] { "CustomerService", "Admin" },
                    "New support ticket",
                    $"Ticket #{ticket.SupportTicketId}: '{ticket.Subject}'.",
                    "Support");
            }
            catch
            {
                // Best-effort notification.
            }

            TempData["SuccessMessage"] = "Your request has been submitted. Our team will reply here shortly.";
            return RedirectToAction(nameof(Ticket), new { id = ticket.SupportTicketId });
        }

        /// <summary>Resident conversation view. Ownership enforced: 404 on mismatch.</summary>
        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Ticket(int id)
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var ticket = await _context.SupportTickets
                .Include(t => t.User)
                .Include(t => t.AssignedToUser)
                .FirstOrDefaultAsync(t => t.SupportTicketId == id);

            if (ticket == null || ticket.UserId != userId)
            {
                return NotFound();
            }

            var messages = await _context.SupportMessages
                .Include(m => m.Sender)
                .Where(m => m.SupportTicketId == id && !m.IsInternal)
                .OrderBy(m => m.SentAt)
                .ToListAsync();

            // Mark staff replies as read (own ticket only).
            var unread = messages.Where(m => !m.IsRead && m.SenderUserId != userId).ToList();
            if (unread.Count > 0)
            {
                foreach (var m in unread)
                {
                    m.IsRead = true;
                }
                await _context.SaveChangesAsync();
            }

            return View(new SupportTicketDetailsViewModel { Ticket = ticket, Messages = messages });
        }

        /// <summary>Non-JS fallback for sending a chat message.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> SendMessage(int id, string message)
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null || ticket.UserId != userId)
            {
                return NotFound();
            }

            // Closed tickets are read-only: reopen explicitly first.
            if (ticket.Status == "Closed")
            {
                TempData["ErrorMessage"] = "This ticket is closed. Reopen it before sending new messages.";
                return RedirectToAction(nameof(Ticket), new { id });
            }

            message = (message ?? string.Empty).Trim();
            if (message.Length == 0 || message.Length > 2000)
            {
                TempData["ErrorMessage"] = "Message must be 1-2000 characters.";
                return RedirectToAction(nameof(Ticket), new { id });
            }

            var now = DateTime.UtcNow;
            _context.SupportMessages.Add(new SupportMessage
            {
                SupportTicketId = id,
                SenderUserId = userId,
                Message = message,
                SentAt = now
            });

            ticket.UpdatedAt = now;
            await _context.SaveChangesAsync();

            try
            {
                await _notifications.CreateNotificationForRolesAsync(
                    new[] { "CustomerService", "Admin" },
                    "New support message",
                    $"Resident reply on ticket #{ticket.SupportTicketId}: '{ticket.Subject}'.",
                    "Support");
            }
            catch
            {
                // Best-effort notification.
            }

            return RedirectToAction(nameof(Ticket), new { id });
        }

        /// <summary>
        /// Resident closes their OWN ticket. Owner comes from server-side
        /// identity only. POST + anti-forgery. History is preserved.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> CloseTicket(int id)
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null || ticket.UserId != userId)
            {
                return NotFound();
            }

            if (ticket.Status == "Closed")
            {
                TempData["AlertMessage"] = "This ticket is already closed.";
                return RedirectToAction(nameof(Ticket), new { id });
            }

            var now = DateTime.UtcNow;
            ticket.Status = "Closed";
            ticket.ClosedAt = now;
            ticket.UpdatedAt = now;
            await _context.SaveChangesAsync();

            await _supportHub.Clients.Group(Hubs.SupportHub.TicketGroup(id))
                .SendAsync("SupportTicketUpdated", new { ticketId = id, status = "Closed" });

            TempData["SuccessMessage"] = $"Ticket #{id} has been closed. The conversation history is preserved.";
            return RedirectToAction(nameof(Ticket), new { id });
        }

        /// <summary>
        /// Resident reopens their OWN closed/resolved ticket explicitly.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> ReopenTicket(int id)
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null || ticket.UserId != userId)
            {
                return NotFound();
            }

            if (ticket.Status != "Closed" && ticket.Status != "Resolved")
            {
                TempData["AlertMessage"] = "This ticket is already open.";
                return RedirectToAction(nameof(Ticket), new { id });
            }

            ticket.Status = "Open";
            ticket.ClosedAt = null;
            ticket.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _supportHub.Clients.Group(Hubs.SupportHub.TicketGroup(id))
                .SendAsync("SupportTicketUpdated", new { ticketId = id, status = "Open" });

            TempData["SuccessMessage"] = $"Ticket #{id} has been reopened.";
            return RedirectToAction(nameof(Ticket), new { id });
        }

        /// <summary>
        /// "Chat with Customer Service": reuse the active conversation if one
        /// exists, otherwise open a new general-inquiry ticket. Prevents
        /// hundreds of duplicate conversations.
        /// </summary>
        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> Chat()
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            // NOTE: explicit status comparisons (no collection Contains).
            // Enumerable.Contains over a string[] cannot be evaluated as a
            // query parameter on this runtime (InvalidOperationException at
            // translation); explicit ORs translate to a plain SQL IN clause.
            var active = await _context.SupportTickets
                .Where(t => t.UserId == userId &&
                    (t.Status == "Open" || t.Status == "In Progress" || t.Status == "Waiting for User"))
                .OrderByDescending(t => t.UpdatedAt)
                .FirstOrDefaultAsync();

            if (active != null)
            {
                return RedirectToAction(nameof(Ticket), new { id = active.SupportTicketId });
            }

            var now = DateTime.UtcNow;
            var ticket = new SupportTicket
            {
                UserId = userId,
                Subject = "General inquiry",
                Description = "Conversation started from customer-service chat.",
                Category = "General Inquiry",
                Priority = "Normal",
                Status = "Open",
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.SupportTickets.Add(ticket);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Ticket), new { id = ticket.SupportTicketId });
        }

        [HttpGet]
        [Authorize(Roles = "Resident")]
        public async Task<IActionResult> UnreadCount()
        {
            if (!TryGetUserId(out var userId))
            {
                return Json(new { count = 0 });
            }
            return Json(new { count = await GetResidentUnreadCountAsync(userId) });
        }

        private Task<int> GetResidentUnreadCountAsync(int userId) =>
            _context.SupportMessages
                .Where(m => !m.IsInternal && !m.IsRead && m.SenderUserId != userId
                    && _context.SupportTickets.Any(t => t.SupportTicketId == m.SupportTicketId && t.UserId == userId))
                .CountAsync();

        // ================= Staff =================

        [HttpGet]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> Dashboard()
        {
            var model = new SupportStaffDashboardViewModel
            {
                OpenCount = await _context.SupportTickets.CountAsync(t => t.Status == "Open"),
                InProgressCount = await _context.SupportTickets.CountAsync(t => t.Status == "In Progress"),
                WaitingCount = await _context.SupportTickets.CountAsync(t => t.Status == "Waiting for User"),
                ResolvedCount = await _context.SupportTickets.CountAsync(t => t.Status == "Resolved"),
                ClosedCount = await _context.SupportTickets.CountAsync(t => t.Status == "Closed"),
                UrgentCount = await _context.SupportTickets
                    .CountAsync(t => t.Priority == "Urgent" && t.Status != "Closed" && t.Status != "Resolved"),
                RecentTickets = await _context.SupportTickets
                    .Include(t => t.User)
                    .OrderByDescending(t => t.UpdatedAt)
                    .Take(10)
                    .ToListAsync()
            };

            var recentIds = model.RecentTickets.Select(t => t.SupportTicketId).ToList();
            ViewBag.UnreadByTicket = await _context.SupportMessages
                .Where(m => recentIds.Contains(m.SupportTicketId) && !m.IsInternal && !m.IsRead
                    && _context.SupportTickets.Any(t => t.SupportTicketId == m.SupportTicketId && t.UserId != m.SenderUserId))
                .GroupBy(m => m.SupportTicketId)
                .ToDictionaryAsync(g => g.Key, g => g.Count());

            if (TryGetUserId(out var me))
            {
                ViewBag.MyAssignedCount = await _context.SupportTickets
                    .CountAsync(t => t.AssignedToUserId == me && t.Status != "Closed" && t.Status != "Resolved");
            }

            return View(model);
        }

        [HttpGet]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> Tickets(string? status = null, string? priority = null,
            string? category = null, string? q = null)
        {
            var query = _context.SupportTickets
                .Include(t => t.User)
                .Include(t => t.AssignedToUser)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Statuses.Contains(status))
            {
                query = query.Where(t => t.Status == status);
            }
            if (!string.IsNullOrWhiteSpace(priority) && Priorities.Contains(priority))
            {
                query = query.Where(t => t.Priority == priority);
            }
            if (!string.IsNullOrWhiteSpace(category) && Categories.Contains(category))
            {
                query = query.Where(t => t.Category == category);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                query = query.Where(t => t.Subject.Contains(q) || t.Description.Contains(q)
                    || (t.User != null && (t.User.FullName.Contains(q) || t.User.UserName!.Contains(q))));
            }

            ViewBag.Statuses = new SelectList(Statuses, status);
            ViewBag.Priorities = new SelectList(Priorities, priority);
            ViewBag.Categories = new SelectList(Categories, category);
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentPriority = priority;
            ViewBag.CurrentCategory = category;
            ViewBag.Query = q;

            return View(await query.OrderByDescending(t => t.UpdatedAt).ToListAsync());
        }

        [HttpGet]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> Manage(int id)
        {
            var ticket = await _context.SupportTickets
                .Include(t => t.User)
                .Include(t => t.AssignedToUser)
                .FirstOrDefaultAsync(t => t.SupportTicketId == id);

            if (ticket == null)
            {
                return NotFound();
            }

            var messages = await _context.SupportMessages
                .Include(m => m.Sender)
                .Where(m => m.SupportTicketId == id)
                .OrderBy(m => m.SentAt)
                .ToListAsync();

            // Mark resident messages as read now that staff opened the ticket.
            if (TryGetUserId(out var me))
            {
                var unread = messages.Where(m => !m.IsRead && !m.IsInternal && m.SenderUserId != me).ToList();
                if (unread.Count > 0)
                {
                    foreach (var m in unread)
                    {
                        m.IsRead = true;
                    }
                    await _context.SaveChangesAsync();
                }
            }

            var staffUsers = await _userManager.GetUsersInRoleAsync("CustomerService");
            var admins = await _userManager.GetUsersInRoleAsync("Admin");
            ViewBag.StaffList = new SelectList(
                staffUsers.Concat(admins).DistinctBy(u => u.Id).ToList(),
                "Id", "FullName", ticket.AssignedToUserId);
            ViewBag.Statuses = new SelectList(Statuses, ticket.Status);
            ViewBag.Priorities = new SelectList(Priorities, ticket.Priority);

            return View(new SupportTicketDetailsViewModel { Ticket = ticket, Messages = messages });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> Reply(int id, string message)
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null)
            {
                return NotFound();
            }

            // Closed tickets are read-only for messaging: reopen via status first.
            if (ticket.Status == "Closed")
            {
                TempData["ErrorMessage"] = "This ticket is closed. Update its status to reopen it before replying.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            message = (message ?? string.Empty).Trim();
            if (message.Length == 0 || message.Length > 2000)
            {
                TempData["ErrorMessage"] = "Reply must be 1-2000 characters.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            var now = DateTime.UtcNow;
            var sender = await _userManager.FindByIdAsync(userId.ToString());
            var chat = new SupportMessage
            {
                SupportTicketId = id,
                SenderUserId = userId,
                Message = message,
                SentAt = now
            };
            _context.SupportMessages.Add(chat);

            if (ticket.Status == "Open")
            {
                ticket.Status = "In Progress";
            }
            ticket.UpdatedAt = now;
            await _context.SaveChangesAsync();

            await _supportHub.Clients.Group(Hubs.SupportHub.TicketGroup(id))
                .SendAsync("SupportMessageReceived", new
                {
                    messageId = chat.SupportMessageId,
                    ticketId = id,
                    senderUserId = userId,
                    senderName = sender?.FullName ?? sender?.UserName ?? "Customer Service",
                    senderIsStaff = true,
                    text = message,
                    sentAt = now.ToString("g")
                });

            try
            {
                await _notifications.CreateNotificationAsync(ticket.UserId,
                    "Customer Service replied",
                    $"Reply on '{ticket.Subject}'.",
                    "Support");
            }
            catch
            {
                // Best-effort notification.
            }

            TempData["SuccessMessage"] = "Reply sent.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> UpdateStatus(int id, string status, string? priority = null, int? assigneeId = null)
        {
            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(status) || !Statuses.Contains(status))
            {
                TempData["ErrorMessage"] = "Invalid status.";
                return RedirectToAction(nameof(Manage), new { id });
            }
            if (priority != null && !Priorities.Contains(priority))
            {
                TempData["ErrorMessage"] = "Invalid priority.";
                return RedirectToAction(nameof(Manage), new { id });
            }
            if (assigneeId.HasValue)
            {
                var assignee = await _userManager.FindByIdAsync(assigneeId.Value.ToString());
                if (assignee == null || !(await _userManager.IsInRoleAsync(assignee, "CustomerService"))
                    && !(await _userManager.IsInRoleAsync(assignee, "Admin")))
                {
                    TempData["ErrorMessage"] = "Assignee must be customer-service staff or admin.";
                    return RedirectToAction(nameof(Manage), new { id });
                }
                ticket.AssignedToUserId = assigneeId;
            }
            if (priority != null)
            {
                ticket.Priority = priority;
            }

            ticket.Status = status;
            ticket.UpdatedAt = DateTime.UtcNow;
            ticket.ClosedAt = status == "Closed" ? DateTime.UtcNow
                : status == "Resolved" ? DateTime.UtcNow
                : null;
            await _context.SaveChangesAsync();

            await _supportHub.Clients.Group(Hubs.SupportHub.TicketGroup(id))
                .SendAsync("SupportTicketUpdated", new { ticketId = id, status });

            if (status is "Resolved" or "Closed")
            {
                try
                {
                    await _notifications.CreateNotificationAsync(ticket.UserId,
                        $"Support ticket {status.ToLower()}",
                        $"Ticket #{ticket.SupportTicketId}: '{ticket.Subject}'.",
                        "Support");
                }
                catch
                {
                    // Best-effort notification.
                }
            }

            TempData["SuccessMessage"] = $"Ticket updated to '{status}'.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> AssignToMe(int id)
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null)
            {
                return NotFound();
            }

            ticket.AssignedToUserId = userId;
            if (ticket.Status == "Open")
            {
                ticket.Status = "In Progress";
            }
            ticket.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Ticket assigned to you.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        /// <summary>Staff-only internal note. Never broadcast to the ticket group.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> AddNote(int id, string message)
        {
            if (!TryGetUserId(out var userId))
            {
                return Challenge();
            }

            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null)
            {
                return NotFound();
            }

            message = (message ?? string.Empty).Trim();
            if (message.Length == 0 || message.Length > 2000)
            {
                TempData["ErrorMessage"] = "Note must be 1-2000 characters.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            var now = DateTime.UtcNow;
            var sender = await _userManager.FindByIdAsync(userId.ToString());
            var note = new SupportMessage
            {
                SupportTicketId = id,
                SenderUserId = userId,
                Message = message,
                SentAt = now,
                IsInternal = true,
                IsRead = true
            };
            _context.SupportMessages.Add(note);
            ticket.UpdatedAt = now;
            await _context.SaveChangesAsync();

            // Staff-only group: the resident never receives this.
            await _supportHub.Clients.Group("support-staff")
                .SendAsync("InternalNoteAdded", new
                {
                    ticketId = id,
                    senderName = sender?.FullName ?? sender?.UserName ?? "Staff",
                    text = message,
                    sentAt = now.ToString("g")
                });

            TempData["SuccessMessage"] = "Internal note saved (resident cannot see it).";
            return RedirectToAction(nameof(Manage), new { id });
        }

        /// <summary>
        /// Staff/Admin close. POST + anti-forgery. Preserves ticket + messages,
        /// sets Closed/ClosedAt/UpdatedAt. Closed tickets become read-only
        /// for messaging (reopen via UpdateStatus).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = StaffRoles)]
        public async Task<IActionResult> CloseTicketStaff(int id)
        {
            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null)
            {
                return NotFound();
            }

            if (ticket.Status == "Closed")
            {
                TempData["AlertMessage"] = "This ticket is already closed.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            var now = DateTime.UtcNow;
            ticket.Status = "Closed";
            ticket.ClosedAt = now;
            ticket.UpdatedAt = now;
            await _context.SaveChangesAsync();

            await _supportHub.Clients.Group(Hubs.SupportHub.TicketGroup(id))
                .SendAsync("SupportTicketUpdated", new { ticketId = id, status = "Closed" });

            try
            {
                await _notifications.CreateNotificationAsync(ticket.UserId,
                    "Support ticket closed",
                    $"Ticket #{ticket.SupportTicketId}: '{ticket.Subject}'.",
                    "Support");
            }
            catch
            {
                // Best-effort notification.
            }

            TempData["SuccessMessage"] = $"Ticket #{id} has been closed.";
            return RedirectToAction(nameof(Manage), new { id });
        }

        /// <summary>
        /// Admin-only permanent delete. POST + anti-forgery only, never GET.
        /// SupportMessages use DeleteBehavior.Cascade, but dependent rows are
        /// also removed explicitly inside a transaction so the delete is safe
        /// even if cascade is ever changed. Users are never deleted.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteTicket(int id)
        {
            var ticket = await _context.SupportTickets.FindAsync(id);
            if (ticket == null)
            {
                return NotFound();
            }

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                var messages = await _context.SupportMessages
                    .Where(m => m.SupportTicketId == id)
                    .ToListAsync();
                if (messages.Count > 0)
                {
                    _context.SupportMessages.RemoveRange(messages);
                }
                _context.SupportTickets.Remove(ticket);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                TempData["ErrorMessage"] = "Could not delete the ticket. Please try again.";
                return RedirectToAction(nameof(Manage), new { id });
            }

            TempData["SuccessMessage"] = $"Ticket #{id} and its conversation history were permanently deleted.";
            return RedirectToAction(nameof(Tickets));
        }
    }

    public class CreateTicketViewModel
    {
        [Required(ErrorMessage = "Subject is required")]
        [StringLength(150, ErrorMessage = "Subject cannot exceed 150 characters")]
        public string Subject { get; set; } = string.Empty;

        [Required(ErrorMessage = "Description is required")]
        [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters")]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Category is required")]
        public string Category { get; set; } = "General Inquiry";

        public string Priority { get; set; } = "Normal";
    }

    public class SupportTicketDetailsViewModel
    {
        public SupportTicket Ticket { get; set; } = null!;

        public List<SupportMessage> Messages { get; set; } = new();
    }

    public class SupportStaffDashboardViewModel
    {
        public int OpenCount { get; set; }

        public int InProgressCount { get; set; }

        public int WaitingCount { get; set; }

        public int ResolvedCount { get; set; }

        public int ClosedCount { get; set; }

        public int UrgentCount { get; set; }

        public List<SupportTicket> RecentTickets { get; set; } = new();
    }
}
