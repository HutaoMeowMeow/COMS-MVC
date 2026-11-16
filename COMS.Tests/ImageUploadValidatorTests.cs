using COMS_MVC.Services;
using SkiaSharp;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for upload validation (fix #1 support): arbitrary paths
/// are never persisted, and only real JPEG/PNG images pass validation.
/// </summary>
public sealed class ImageUploadValidatorTests
{
    [Theory]
    [InlineData("evil.gif", "image/gif")]
    [InlineData("video.mp4", "video/mp4")]
    [InlineData("photo.svg", "image/svg+xml")]
    [InlineData("photo.jpg.exe", "image/jpeg")]
    [InlineData(null, "image/jpeg")]
    public void Header_RejectsDisallowedExtensions(string? fileName, string mime)
    {
        Assert.NotNull(ImageUploadValidator.ValidateHeader(fileName, mime, 1024));
    }

    [Fact]
    public void Header_RejectsOversize()
    {
        Assert.Equal(
            ImageUploadValidator.ErrorTooBig,
            ImageUploadValidator.ValidateHeader("a.jpg", "image/jpeg", ImageUploadValidator.MaxFileSizeBytes + 1));
    }

    [Fact]
    public void Header_RejectsEmpty()
    {
        Assert.NotNull(ImageUploadValidator.ValidateHeader("a.jpg", "image/jpeg", 0));
    }

    [Fact]
    public void Header_RejectsMismatchedMime()
    {
        Assert.NotNull(ImageUploadValidator.ValidateHeader("a.jpg", "application/octet-stream", 100));
    }

    [Theory]
    [InlineData("photo.JPG", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("photo.png", "image/png")]
    public void Header_AcceptsValidJpegPng(string fileName, string mime)
    {
        Assert.Null(ImageUploadValidator.ValidateHeader(fileName, mime, 1024));
    }

    [Fact]
    public void Content_RejectsRenamedGif()
    {
        // Real GIF bytes with a .png name: magic-byte check must reject.
        var gif = "GIF89a"u8.ToArray()
            .Concat(new byte[] { 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 1, 0, 0x3B })
            .ToArray();
        Assert.Equal(ImageUploadValidator.ErrorNotImage, ImageUploadValidator.ValidateContent(gif));
    }

    [Fact]
    public void Content_RejectsGarbageBytes()
    {
        Assert.Equal(
            ImageUploadValidator.ErrorNotImage,
            ImageUploadValidator.ValidateContent("this is not an image at all, just text"u8.ToArray()));
    }

    [Fact]
    public void Content_AcceptsRealPng()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        Assert.Null(ImageUploadValidator.ValidateContent(bytes));
    }

    [Fact]
    public void Content_AcceptsRealJpeg()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        var bytes = data.ToArray();
        Assert.Null(ImageUploadValidator.ValidateContent(bytes));
    }

    [Fact]
    public void Content_RejectsTinyImage()
    {
        using var bitmap = new SKBitmap(8, 8);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        Assert.Equal(ImageUploadValidator.ErrorUnreadable, ImageUploadValidator.ValidateContent(bytes));
    }

    [Fact]
    public void Content_AcceptsDimensionsAtBudget()
    {
        // 4096 x 100: exactly at the cap in one axis, valid dimensions.
        using var bitmap = new SKBitmap(ImageUploadValidator.MaxPixelDimension, 100);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        Assert.Null(ImageUploadValidator.ValidateContent(bytes));
    }

    [Fact]
    public void Content_RejectsOversizedWidth()
    {
        // 4097 pixels wide: header-peek must reject before decoding.
        using var bitmap = new SKBitmap(ImageUploadValidator.MaxPixelDimension + 1, 100);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        Assert.Equal(ImageUploadValidator.ErrorTooLarge, ImageUploadValidator.ValidateContent(bytes));
    }

    [Fact]
    public void ExceedsPixelBudget_MatchesConstantsBoundaries()
    {
        var max = ImageUploadValidator.MaxPixelDimension;
        Assert.False(ImageUploadValidator.ExceedsPixelBudget(max, max));
        Assert.True(ImageUploadValidator.ExceedsPixelBudget(max + 1, 1));
        Assert.True(ImageUploadValidator.ExceedsPixelBudget(1, max + 1));
    }

    [Fact]
    public void Content_DecompressionBomb_IsRejectedFromHeaderWithoutFullDecode()
    {
        // A tiny PNG whose IHDR declares 20000x20000 (~1.6GB RGBA). Decoding the
        // full surface before checking dimensions is the native/OOM crash vector:
        // ValidateContent must reject it from the header alone.
        var bomb = MakeBombPng(20000, 20000);
        Assert.True(bomb.Length < 1024, "bomb should be tiny");

        var error = ImageUploadValidator.ValidateContent(bomb);

        Assert.Equal(ImageUploadValidator.ErrorTooLarge, error);
    }

    [Fact]
    public void TryGetDeclaredSize_ReadsHeaderWithoutAllocatingPixels()
    {
        var bomb = MakeBombPng(20000, 20000);

        Assert.True(ImageUploadValidator.TryGetDeclaredSize(bomb, out var w, out var h));
        Assert.Equal(20000, w);
        Assert.Equal(20000, h);
    }

    [Fact]
    public void TryGetDeclaredSize_ReturnsFalseForNonImage()
    {
        Assert.False(ImageUploadValidator.TryGetDeclaredSize("not an image"u8.ToArray(), out _, out _));
        Assert.False(ImageUploadValidator.TryGetDeclaredSize(Array.Empty<byte>(), out _, out _));
    }

    private static byte[] MakeBombPng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        WriteChunk(ms, "IHDR", ihdr);
        var raw = new byte[] { 0, 0, 0, 0, 0 };
        using (var z = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true))
                zlib.Write(raw, 0, raw.Length);
            WriteChunk(ms, "IDAT", z.ToArray());
        }
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteInt(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        len[0] = (byte)(data.Length >> 24); len[1] = (byte)(data.Length >> 16); len[2] = (byte)(data.Length >> 8); len[3] = (byte)data.Length;
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = Crc32(t, data);
        var c = new byte[4];
        c[0] = (byte)(crc >> 24); c[1] = (byte)(crc >> 16); c[2] = (byte)(crc >> 8); c[3] = (byte)crc;
        s.Write(c);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        foreach (var b in data) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    [Fact]
    public async Task ValidateAsync_EndToEnd_AcceptsRealPngUpload()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var ms = new MemoryStream(bytes);
        ms.Position = 0;

        var file = new FormFileShim(bytes, "photo.png", "image/png");
        var (resultBytes, ext, error) = await ImageUploadValidator.ValidateAsync(file);

        Assert.Null(error);
        Assert.NotNull(resultBytes);
        Assert.Equal(".png", ext);
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
