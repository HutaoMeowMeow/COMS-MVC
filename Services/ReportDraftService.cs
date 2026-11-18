using Microsoft.EntityFrameworkCore;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Values accepted from the report form while a draft is being autosaved.
    /// Partial input is valid at draft stage: only lengths and the optional
    /// photo attachment are enforced here. Full validation happens on submit.
    /// </summary>
    public sealed class ReportDraftInput
    {
        public int? CanalId { get; set; }

        public string? ReportType { get; set; }

        public string? Title { get; set; }

        public string? Description { get; set; }

        public string? Location { get; set; }

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }
    }

    public sealed class ReportDraftSaveResult
    {
        public ReportDraft? Draft { get; set; }

        /// <summary>
        /// Non-fatal problem (for example a rejected photo). The rest of the
        /// draft is still saved, so the resident never loses their work.
        /// </summary>
        public string? Warning { get; set; }
    }

    public interface IReportDraftService
    {
        Task<ReportDraft?> GetForUserAsync(int userId, CancellationToken ct = default);

        Task<ReportDraft?> GetOwnedAsync(int draftId, int userId, CancellationToken ct = default);

        Task<ReportDraftSaveResult> SaveAsync(
            int userId,
            ReportDraftInput input,
            IFormFile? photo,
            bool removePhoto,
            CancellationToken ct = default);

        Task<bool> DeleteAsync(int draftId, int userId, CancellationToken ct = default);

        Task DeleteForUserAsync(int userId, CancellationToken ct = default);
    }

    /// <summary>
    /// Server-side persistence for report drafts. Drafts live in the SQLite /
    /// PostgreSQL database (never only in the browser), photos are stored under
    /// wwwroot/uploads/drafts until the report is submitted, and every read or
    /// write is scoped to the authenticated owner.
    /// </summary>
    public sealed class ReportDraftService : IReportDraftService
    {
        public const string DraftUploadFolder = "App_Data/draft-uploads";

        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ReportDraftService> _logger;

        public ReportDraftService(
            ApplicationDbContext context,
            IWebHostEnvironment env,
            ILogger<ReportDraftService> logger)
        {
            _context = context;
            _env = env;
            _logger = logger;
        }

        public Task<ReportDraft?> GetForUserAsync(int userId, CancellationToken ct = default)
            => _context.ReportDrafts
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.UserId == userId, ct);

        public Task<ReportDraft?> GetOwnedAsync(int draftId, int userId, CancellationToken ct = default)
            => _context.ReportDrafts
                .FirstOrDefaultAsync(d => d.ReportDraftId == draftId && d.UserId == userId, ct);

        public async Task<ReportDraftSaveResult> SaveAsync(
            int userId,
            ReportDraftInput input,
            IFormFile? photo,
            bool removePhoto,
            CancellationToken ct = default)
        {
            var result = new ReportDraftSaveResult();
            var draft = await _context.ReportDrafts
                .FirstOrDefaultAsync(d => d.UserId == userId, ct);

            string? newPhotoPath = null;
            string? newPhotoName = null;
            string? newPhotoExt = null;
            byte[]? photoBytes = null;

            if (photo != null && photo.Length > 0)
            {
                var (bytes, ext, error) = await ImageUploadValidator.ValidateAsync(photo, ct);
                if (error != null)
                {
                    // The photo is rejected, but the text fields are still saved.
                    result.Warning = error;
                }
                else
                {
                    photoBytes = bytes;
                    newPhotoExt = ext;
                }
            }

            var title = Trim(input.Title, 150);
            var description = Trim(input.Description, 2000);
            var location = Trim(input.Location, 200);
            var reportType = Trim(input.ReportType, 100);
            var canalId = input.CanalId;
            if (canalId.HasValue)
            {
                var canalExists = await _context.Canals
                    .AsNoTracking()
                    .AnyAsync(c => c.CanalId == canalId.Value && c.Status != "Inactive", ct);
                if (!canalExists)
                {
                    canalId = null;
                }
            }

            var hasContent = photoBytes != null
                || !string.IsNullOrWhiteSpace(title)
                || !string.IsNullOrWhiteSpace(description)
                || !string.IsNullOrWhiteSpace(location)
                || input.Latitude.HasValue
                || input.Longitude.HasValue;

            if (draft == null)
            {
                if (!hasContent)
                {
                    // Nothing worth persisting yet (a pristine form must not
                    // create an empty draft row).
                    return result;
                }
                draft = new Models.ReportDraft
                {
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.ReportDrafts.Add(draft);
            }

            var oldPhotoPath = draft.PhotoPath;

            if (photoBytes != null)
            {
                try
                {
                    var saved = await SaveDraftPhotoAsync(photoBytes, newPhotoExt!, ct);
                    newPhotoPath = saved;
                    newPhotoName = Path.GetFileName(photo!.FileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not persist a report draft photo.");
                    result.Warning ??= "The photo could not be saved. Your other draft details were kept.";
                }
            }

            draft.CanalId = canalId;
            draft.ReportType = string.IsNullOrWhiteSpace(reportType) ? "Garbage / Debris" : reportType;
            draft.Title = title;
            draft.Description = string.IsNullOrWhiteSpace(description) ? null : description;
            draft.Location = string.IsNullOrWhiteSpace(location) ? null : location;
            draft.Latitude = NormalizeCoordinate(input.Latitude, 90);
            draft.Longitude = NormalizeCoordinate(input.Longitude, 180);
            if (newPhotoPath != null)
            {
                draft.PhotoPath = newPhotoPath;
                draft.PhotoFileName = newPhotoName;
            }
            else if (removePhoto)
            {
                draft.PhotoPath = null;
                draft.PhotoFileName = null;
            }
            draft.UpdatedAt = DateTime.UtcNow;

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Unique index race (two autosave requests from the same tab):
                // reload the row and update it instead of inserting a second one.
                _context.ChangeTracker.Clear();
                draft = await _context.ReportDrafts.FirstOrDefaultAsync(d => d.UserId == userId, ct);
                if (draft == null)
                {
                    result.Warning ??= "Draft could not be saved. Please try again.";
                    return result;
                }
                draft.CanalId = canalId;
                draft.ReportType = string.IsNullOrWhiteSpace(reportType) ? "Garbage / Debris" : reportType;
                draft.Title = title;
                draft.Description = string.IsNullOrWhiteSpace(description) ? null : description;
                draft.Location = string.IsNullOrWhiteSpace(location) ? null : location;
                draft.Latitude = NormalizeCoordinate(input.Latitude, 90);
                draft.Longitude = NormalizeCoordinate(input.Longitude, 180);
                if (newPhotoPath != null)
                {
                    draft.PhotoPath = newPhotoPath;
                    draft.PhotoFileName = newPhotoName;
                }
                else if (removePhoto)
                {
                    draft.PhotoPath = null;
                    draft.PhotoFileName = null;
                }
                draft.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
            }

            // Old attachment is removed only after the new state is persisted.
            if (oldPhotoPath != null && oldPhotoPath != draft.PhotoPath)
            {
                DeleteDraftPhoto(oldPhotoPath);
            }

            result.Draft = draft;
            return result;
        }

        public async Task<bool> DeleteAsync(int draftId, int userId, CancellationToken ct = default)
        {
            var draft = await _context.ReportDrafts
                .FirstOrDefaultAsync(d => d.ReportDraftId == draftId && d.UserId == userId, ct);
            if (draft == null)
            {
                return false;
            }
            var photoPath = draft.PhotoPath;
            _context.ReportDrafts.Remove(draft);
            await _context.SaveChangesAsync(ct);
            if (photoPath != null)
            {
                DeleteDraftPhoto(photoPath);
            }
            return true;
        }

        public async Task DeleteForUserAsync(int userId, CancellationToken ct = default)
        {
            var draft = await _context.ReportDrafts
                .FirstOrDefaultAsync(d => d.UserId == userId, ct);
            if (draft == null)
            {
                return;
            }
            var photoPath = draft.PhotoPath;
            _context.ReportDrafts.Remove(draft);
            await _context.SaveChangesAsync(ct);
            if (photoPath != null)
            {
                DeleteDraftPhoto(photoPath);
            }
        }

        /// <summary>
        /// Resolves a stored draft attachment (file name, or a legacy URL path)
        /// to an absolute path inside the private draft folder. Draft images are
        /// never served from wwwroot: they are streamed only through the
        /// owner-checked DraftPhoto endpoint.
        /// </summary>
        public static string? ResolveDraftPhotoDiskPath(string? contentRootPath, string? photoPath)
        {
            if (string.IsNullOrWhiteSpace(contentRootPath) || string.IsNullOrWhiteSpace(photoPath))
            {
                return null;
            }
            var fileName = Path.GetFileName(photoPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }
            var folder = Path.GetFullPath(Path.Combine(contentRootPath, DraftUploadFolder));
            var combined = Path.GetFullPath(Path.Combine(folder, fileName));
            var rootPrefix = folder + Path.DirectorySeparatorChar;
            return combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? combined
                : null;
        }

        private async Task<string> SaveDraftPhotoAsync(byte[] bytes, string extension, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_env.ContentRootPath))
            {
                throw new InvalidOperationException("Content root is not configured.");
            }
            var folder = Path.Combine(_env.ContentRootPath, DraftUploadFolder);
            Directory.CreateDirectory(folder);
            var uniqueName = $"draft_{Guid.NewGuid():N}{extension}";
            var filePath = Path.Combine(folder, uniqueName);
            await File.WriteAllBytesAsync(filePath, bytes, ct);
            return uniqueName;
        }

        private void DeleteDraftPhoto(string photoPath)
        {
            try
            {
                var diskPath = ResolveDraftPhotoDiskPath(_env.ContentRootPath, photoPath);
                if (diskPath != null && File.Exists(diskPath))
                {
                    File.Delete(diskPath);
                }
            }
            catch (Exception ex)
            {
                // Best-effort cleanup; the database no longer references the file.
                _logger.LogDebug(ex, "Draft photo cleanup failed.");
            }
        }

        private static string Trim(string? value, int maxLength)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private static double? NormalizeCoordinate(double? value, double limit)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                return null;
            }
            return value.Value < -limit || value.Value > limit ? null : value.Value;
        }
    }
}

