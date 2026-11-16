using COMS_MVC.Services;
using SkiaSharp;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for the customer-service image-upload crash.
///
/// Root cause being guarded: SkiaSharp's native pixel accessors
/// (<c>SKBitmap.GetPixel</c> / <c>GetPixels</c>) can raise an
/// AccessViolationException on unusual decoded color types, and SkiaSharp is
/// not safe for concurrent native use. AccessViolationException is a
/// corrupted-process-state exception: it is not delivered to ordinary catch
/// blocks, so the process terminated with exit code 4294967295 (0xffffffff).
///
/// These tests drive the pipeline concurrently with the adversarial inputs
/// (grayscale, 16-bit, truncated, garbage, empty) that reach the pixel
/// readers, and assert nothing escapes.
/// </summary>
public sealed class SkiaSharpConcurrencyTests
{
    private static byte[] Encode(SKBitmap bmp, SKEncodedImageFormat fmt, int quality = 100)
    {
        using var data = bmp.Encode(fmt, quality);
        return data.ToArray();
    }

    private static byte[] ValidPng(int w = 300, int h = 200)
    {
        using var b = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                b.SetPixel(x, y, new SKColor((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256)));
        return Encode(b, SKEncodedImageFormat.Png);
    }

    private static byte[] ValidJpeg(int w = 300, int h = 200)
    {
        using var b = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                b.SetPixel(x, y, new SKColor((byte)(x % 256), (byte)(y % 256), 90));
        return Encode(b, SKEncodedImageFormat.Jpeg, 90);
    }

    /// <summary>Grayscale PNG — decodes to SKColorType.Gray8.</summary>
    private static byte[] Gray8Png(int w = 300, int h = 200)
    {
        var info = new SKImageInfo(w, h, SKColorType.Gray8);
        using var b = new SKBitmap(info);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = (byte)((x + y) % 256);
                b.SetPixel(x, y, new SKColor(v, v, v));
            }
        return Encode(b, SKEncodedImageFormat.Png);
    }

    /// <summary>16-bit-per-channel PNG — decodes to SKColorType.RgbaF16.</summary>
    private static byte[] RgbaF16Png(int w = 300, int h = 200)
    {
        var info = new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul);
        using var b = new SKBitmap(info);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                b.SetPixel(x, y, new SKColor((byte)(x % 256), (byte)(y % 256), 128, 255));
        return Encode(b, SKEncodedImageFormat.Png);
    }

    private static byte[] Truncated(byte[] full) => full.Take(full.Length / 2).ToArray();

    [Fact]
    public async Task ImagePipeline_SurvivesConcurrentMixedAndAdversarialInputs()
    {
        var png = ValidPng();
        var jpeg = ValidJpeg();

        var inputs = new List<byte[]>
        {
            png,
            jpeg,
            Gray8Png(),
            RgbaF16Png(),
            Array.Empty<byte>(),
            "this is definitely not an image"u8.ToArray(),
            new byte[] { 0xFF, 0xD8, 0xFF, 0x00 },   // JPEG magic, no real data
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },   // PNG magic, no real data
            Truncated(png),
            Truncated(jpeg),
        };

        var relevance = new ImageRelevanceService();

        // 64 concurrent operations across all input shapes: this is the
        // condition that previously crashed the process.
        var tasks = new List<Task>();
        for (var i = 0; i < 64; i++)
        {
            var bytes = inputs[i % inputs.Count];
            tasks.Add(Task.Run(async () =>
            {
                var result = await relevance.AnalyzeAsync(bytes);
                Assert.NotNull(result);
                Assert.False(string.IsNullOrWhiteSpace(result.Status));

                var (decoded, ext, error) = await ImageUploadValidator.ValidateAsync(
                    new FormFileShim(bytes, "photo.jpg", "image/jpeg"));
                // Every input must be either accepted or rejected with a
                // message — never an escaping exception.
                if (error == null)
                {
                    Assert.NotNull(decoded);
                    Assert.NotNull(ext);
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void PixelBuffer_ConvertsUnusualColorTypes_ToManagedRgba()
    {
        // Gray8 and RgbaF16 are exactly the color types whose per-pixel
        // native reads produced AccessViolationException crashes.
        foreach (var png in new[] { Gray8Png(), RgbaF16Png(), ValidPng() })
        {
            using var bitmap = SKBitmap.Decode(png);
            Assert.NotNull(bitmap);

            Assert.True(SkiaPixelBuffer.TryCopy(bitmap, out var rgba, out var width, out var height, out var stride));
            Assert.Equal(bitmap.Width, width);
            Assert.Equal(bitmap.Height, height);
            Assert.Equal(stride * height, rgba.Length);
            Assert.True(rgba.Length > 0);
        }
    }

    [Fact]
    public async Task RelevanceAnalysis_NeverThrows_OnAdversarialBytes()
    {
        var relevance = new ImageRelevanceService();
        var inputs = new List<byte[]>
        {
            Array.Empty<byte>(),
            new byte[] { 0xFF },
            "garbage"u8.ToArray(),
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 },
            Truncated(ValidPng()),
            Gray8Png(),
            RgbaF16Png(),
        };

        foreach (var bytes in inputs)
        {
            var result = await relevance.AnalyzeAsync(bytes);
            Assert.NotNull(result);
            Assert.Contains(result.Status, new[] { "Relevant", "Irrelevant", "Uncertain" });
        }
    }

    /// <summary>Minimal IFormFile stand-in without ASP.NET hosting.</summary>
    private sealed class FormFileShim : Microsoft.AspNetCore.Http.IFormFile
    {
        private readonly byte[] _bytes;
        public FormFileShim(byte[] bytes, string name, string contentType)
        {
            _bytes = bytes;
            FileName = name;
            ContentType = contentType;
            Length = bytes.Length;
            Name = "file";
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary();
            ContentDisposition = $"form-data; name=file; filename={name}";
        }
        public string ContentDisposition { get; }
        public string ContentType { get; }
        public string FileName { get; }
        public Microsoft.AspNetCore.Http.IHeaderDictionary Headers { get; }
        public long Length { get; }
        public string Name { get; }
        public void CopyTo(Stream target) => target.Write(_bytes, 0, _bytes.Length);
        public Task CopyToAsync(Stream target, CancellationToken ct = default)
            => target.WriteAsync(_bytes, 0, _bytes.Length, ct);
        public Stream OpenReadStream() => new MemoryStream(_bytes);
    }
}
