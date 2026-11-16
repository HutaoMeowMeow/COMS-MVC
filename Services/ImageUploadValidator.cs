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
        /// Reads the DECLARED pixel dimensions from the encoded header only, using
        /// <see cref="SKCodec"/> (no pixel buffer is allocated). Returns false when
        /// the bytes cannot be parsed as an image at all. This is the only safe way
        /// to reject a decompression bomb (a tiny file whose header declares
        /// enormous dimensions) before any full decode: <see cref="SKBitmap.Decode"/>
        /// allocates the whole surface, so a header-valid 5MB file can otherwise
        /// force a multi-gigabyte native allocation (and a native/OOM crash) that
        /// managed try/catch cannot contain.
        /// </summary>
        public static bool TryGetDeclaredSize(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            if (bytes == null || bytes.Length == 0) return false;
            try
            {
                // All SkiaSharp native work (even header parsing) runs inside the
                // process-wide gate.
                var dims = SkiaSharpGate.Run(() =>
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    using var codec = SKCodec.Create(stream);
                    if (codec == null) return (ok: false, w: 0, h: 0);
                    var info = codec.Info;
                    return (ok: true, w: info.Width, h: info.Height);
                });
                if (!dims.ok || dims.w <= 0 || dims.h <= 0) return false;
                width = dims.w;
                height = dims.h;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Content checks over the raw bytes: magic bytes + real decode + format
        /// must be JPEG or PNG (rejects renamed GIF/video/corrupt files). The
        /// DECLARED dimensions are read from the header first and the upload is
        /// rejected before any full pixel allocation, so a decompression bomb is
        /// never decoded. Only then is the bitmap decoded (bounded to the allowed
        /// pixel budget) to prove the file is genuinely decodable.
        /// </summary>
        public static string? ValidateContent(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return ErrorNotImage;
            if (bytes.Length > MaxFileSizeBytes)
                return ErrorTooBig;
            if (!HasAllowedMagic(bytes))
                return ErrorNotImage;

            // 1) Header-only dimension check (no allocation) — rejects bombs first.
            if (!TryGetDeclaredSize(bytes, out var declaredWidth, out var declaredHeight))
                return ErrorUnreadable;
            if (ExceedsPixelBudget(declaredWidth, declaredHeight))
                return ErrorTooLarge;

            try
            {
                // 2) Full decode only for images inside the pixel budget, and all
                // native SkiaSharp work (decode and dispose) runs inside the
                // process-wide gate: disposing a native bitmap outside the gate
                // can race with other image operations and crash the process with
                // an uncatchable AccessViolationException (exit 0xffffffff).
                return SkiaSharpGate.Run(() =>
                {
                    using var bitmap = SKBitmap.Decode(bytes);
                    if (bitmap == null)
                        return ErrorUnreadable;

                    if (bitmap.Width < 16 || bitmap.Height < 16)
                        return ErrorUnreadable;
                    // Defensive re-check against the actual decoded size.
                    if (ExceedsPixelBudget(bitmap.Width, bitmap.Height))
                        return ErrorTooLarge;
                    return (string?)null;
                });
            }
            catch
            {
                return ErrorUnreadable;
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
