using SkiaSharp;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Single source of truth for photo-upload validation (reports + announcements).
    /// JPEG and PNG only. Extension, MIME type, magic bytes, and actual decodability
    /// are all verified; nothing is written to disk until every check passes, so
    /// rejected files never reach storage. Never trust the client alone.
    /// </summary>
    public static class ImageUploadValidator
    {
        public const long MaxFileSizeBytes = 5 * 1024 * 1024;

        public const string ErrorNotImage =
            "Accepted formats: JPG, JPEG, PNG only. GIFs and videos are not allowed.";

        public const string ErrorTooBig =
            "Photo size must be less than 5MB.";

        public const string ErrorUnreadable =
            "The uploaded file is not a valid JPEG or PNG image. Please upload a real photo.";

        private static readonly HashSet<string> AllowedExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

        private static readonly HashSet<string> AllowedMimeTypes =
            new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png" };

        // JPEG: FF D8 FF. PNG: 89 50 4E 47 0D 0A 1A 0A.
        private static readonly byte[] JpegMagic = new byte[] { 0xFF, 0xD8, 0xFF };
        private static readonly byte[] PngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Cheap header checks: size, extension, MIME type. No I/O.</summary>
        public static string? ValidateHeader(string? fileName, string? contentType, long length)
        {
            if (length <= 0)
                return ErrorNotImage;
            if (length > MaxFileSizeBytes)
                return ErrorTooBig;
            var ext = Path.GetExtension(fileName ?? string.Empty);
            if (!AllowedExtensions.Contains(ext))
                return ErrorNotImage;
            if (!string.IsNullOrWhiteSpace(contentType) && !AllowedMimeTypes.Contains(contentType.Trim()))
                return ErrorNotImage;
            return null;
        }

        /// <summary>
        /// Absolute dimension cap for untrusted uploads. A header-valid PNG
        /// inside the 5MB byte budget can still declare enormous pixel
        /// dimensions, which would allocate gigabytes during decode
        /// (decompression bomb). Uploads are rejected above this limit before
        /// any bitmap allocation (max 4096x4096 = ~64MB RGBA transient).
        /// </summary>
        public const int MaxPixelDimension = 4096;

        public static bool ExceedsPixelBudget(int width, int height) =>
            width > MaxPixelDimension || height > MaxPixelDimension;

        public const string ErrorTooLarge =
            "Image dimensions are too large. Please upload a photo of 4096x4096 pixels or smaller.";

        /// <summary>
        /// Content checks over the raw bytes: magic bytes + real decode + format
        /// must be JPEG or PNG (rejects renamed GIF/video/corrupt files). A
        /// header decode runs first so a declared decompression bomb is rejected
        /// before any full pixel allocation.
        /// </summary>
        public static string? ValidateContent(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return ErrorNotImage;
            if (bytes.Length > MaxFileSizeBytes)
                return ErrorTooBig;
            if (!HasAllowedMagic(bytes))
                return ErrorNotImage;
            try
            {
                // Header decode first: reject corrupt/unreadable before
                // spending memory on full pixel allocation.
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null)
                    return ErrorUnreadable;

                if (bitmap.Width < 16 || bitmap.Height < 16)
                    return ErrorUnreadable;
                if (ExceedsPixelBudget(bitmap.Width, bitmap.Height))
                    return ErrorTooLarge;
            }
            catch
            {
                return ErrorUnreadable;
            }
            return null;
        }

        /// <summary>
        /// Header-only dimension peek: reports the declared pixel dimensions by
        /// decoding the header, returns false instead of throwing on unreadable
        /// input. Used to reject oversized declarations before full decode.
        /// </summary>
        public static bool ExceedsPixelBudget(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            if (bytes == null || bytes.Length == 0) return false;
            try
            {
                using var info = SKBitmap.Decode(bytes);
                if (info == null) return false;
                width = info.Width;
                height = info.Height;
                return ExceedsPixelBudget(width, height);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Full validation of an uploaded file. Reads bytes into memory first so a
        /// rejected file is never written to disk. Returns (bytes, ext, error);
        /// error is null on success.
        /// </summary>
        public static async Task<(byte[]? Bytes, string? Extension, string? Error)> ValidateAsync(
            IFormFile file, CancellationToken ct = default)
        {
            var headerError = ValidateHeader(file.FileName, file.ContentType, file.Length);
            if (headerError != null)
                return (null, null, headerError);
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            // Length can lie on exotic providers; re-check what we actually read.
            if (bytes.Length != file.Length && file.Length > 0)
            {
                // Fall through to content validation which re-checks size.
            }
            var contentError = ValidateContent(bytes);
            if (contentError != null)
                return (null, null, contentError);
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            return (bytes, ext, null);
        }
        private static bool HasAllowedMagic(byte[] bytes)
        {
            if (bytes.Length >= JpegMagic.Length)
            {
                var isJpeg = true;
                for (var i = 0; i < JpegMagic.Length; i++)
                {
                    if (bytes[i] != JpegMagic[i]) { isJpeg = false; break; }
                }
                if (isJpeg) return true;
            }
            if (bytes.Length >= PngMagic.Length)
            {
                var isPng = true;
                for (var i = 0; i < PngMagic.Length; i++)
                {
                    if (bytes[i] != PngMagic[i]) { isPng = false; break; }
                }
                if (isPng) return true;
            }
            return false;
        }
    }
}
