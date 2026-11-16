using SkiaSharp;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Process-wide serialization for all SkiaSharp native work.
    ///
    /// Why this exists: SkiaSharp's native (libSkiaSharp) decoders and pixel
    /// accessors are not safe to run concurrently from multiple threads. When
    /// two image operations overlap — e.g. a resident ticket upload analyzing
    /// a photo on a request thread while a community report's background
    /// authenticity check decodes on a thread-pool thread — the native layer
    /// can raise an AccessViolationException. That exception is a
    /// corrupted-process-state exception: the CLR does not deliver it to
    /// ordinary catch blocks, and the process terminates with exit code
    /// 4294967295 (0xffffffff). Serializing every native call through this
    /// single gate removes that failure mode without changing any image
    /// semantics.
    ///
    /// The gate is a plain monitor lock because all SkiaSharp work here is
    /// synchronous and CPU-bound; there are no awaits inside, so no deadlock
    /// is possible. C# locks are re-entrant, so nested wrapping is safe.
    /// </summary>
    internal static class SkiaSharpGate
    {
        private static readonly object Gate = new object();

        public static T Run<T>(Func<T> action)
        {
            lock (Gate)
            {
                return action();
            }
        }

        public static void Run(Action action)
        {
            lock (Gate)
            {
                action();
            }
        }
    }

    /// <summary>
    /// Safe access to a decoded bitmap's pixels: one bulk copy out of native
    /// memory into a managed RGBA buffer, instead of calling
    /// <see cref="SKBitmap.GetPixel"/> per pixel.
    ///
    /// Per-pixel <c>GetPixel</c> performs native pointer arithmetic for every
    /// call. On images whose decoded color type is unusual (grayscale,
    /// 16-bit, CMYK, indexed), that arithmetic is the documented source of
    /// AccessViolationException crashes that take the whole process down.
    /// Converting once to Rgba8888 and copying the bytes out removes the
    /// per-pixel native surface entirely, and makes large images far faster.
    /// </summary>
    internal static class SkiaPixelBuffer
    {
        /// <summary>
        /// Copies the bitmap into a managed R,G,B,A byte buffer.
        /// Returns false (never throws) when the image cannot be converted.
        /// </summary>
        public static bool TryCopy(SKBitmap bitmap, out byte[] rgba, out int width, out int height, out int stride)
        {
            rgba = Array.Empty<byte>();
            width = height = stride = 0;

            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return false;
            }

            try
            {
                using var converted = bitmap.Copy(SKColorType.Rgba8888);
                if (converted == null || converted.Width <= 0 || converted.Height <= 0)
                {
                    return false;
                }

                stride = converted.RowBytes;
                width = converted.Width;
                height = converted.Height;

                if (stride <= 0 || height <= 0)
                {
                    return false;
                }

                // Hard ceiling: never allocate more than 256MB for analysis.
                var bytes = (long)stride * height;
                if (bytes <= 0 || bytes > 256L * 1024 * 1024)
                {
                    return false;
                }

                // GetPixelSpan wraps the native pixel block; ToArray copies it
                // into managed memory in one operation.
                var span = converted.GetPixelSpan();
                if (span.Length < bytes)
                {
                    return false;
                }

                rgba = span.ToArray();
                return rgba.Length >= bytes;
            }
            catch
            {
                rgba = Array.Empty<byte>();
                width = height = stride = 0;
                return false;
            }
        }
    }
}
