using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using COMS_MVC.Data;
using COMS_MVC.Models;
using System.IO;
using System.Threading.Tasks;

namespace COMS_MVC.Controllers
{
    [Authorize]
    [Route("Images")]
    public class ImagesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public ImagesController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        /// <summary>
        /// Serves an uploaded ticket image with ownership authorization.
        /// Only the ticket owner, CustomerService role, or Admin can access.
        /// </summary>
        [HttpGet("{ticketId}")]
        public async Task<IActionResult> GetImage(int ticketId)
        {
            var ticket = await _context.SupportTickets.FindAsync(ticketId);
            if (ticket == null)
            {
                return NotFound();
            }

            // Check authorization: ticket owner, staff, or admin
            var userId = _context.Users.FirstOrDefault(u => u.UserName == User.Identity.Name)?.Id;
            bool isOwner = ticket.UserId == userId;
            bool isStaff = User.IsInRole("CustomerService") || User.IsInRole("Admin");

            if (!isOwner && !isStaff)
            {
                return Forbid();
            }

            if (string.IsNullOrEmpty(ticket.ImagePath))
            {
                return NotFound();
            }

            // Construct the full file path - store outside web root
            // ImagePath format: /uploads/tickets/{filename}
            // We need to find the file in wwwroot/uploads/tickets/
            if (string.IsNullOrWhiteSpace(_env.WebRootPath))
            {
                return NotFound();
            }

            string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "tickets");

            // Extract just the filename from the path
            string fileName = Path.GetFileName(ticket.ImagePath);

            string fullPath = Path.Combine(uploadsFolder, fileName);

            // Security: prevent path traversal
            if (!Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(uploadsFolder), StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound();
            }

            byte[] fileBytes;
            try
            {
                fileBytes = await System.IO.File.ReadAllBytesAsync(fullPath);
            }
            catch (IOException)
            {
                // File vanished or became unreadable between the existence
                // check and the read; report it as missing rather than crashing.
                return NotFound();
            }
            var contentType = GetContentType(ticket.ImageExtension);

            return File(fileBytes, contentType);
        }

        private static string GetContentType(string? extension)
        {
            if (string.IsNullOrEmpty(extension)) return "application/octet-stream";

            switch (extension.ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
                default:
                    return "application/octet-stream";
            }
        }
    }
}