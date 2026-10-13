using COMS_MVC.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
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
        using var image = new Image<Rgba32>(64, 64);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        Assert.Null(ImageUploadValidator.ValidateContent(ms.ToArray()));
    }

    [Fact]
    public void Content_AcceptsRealJpeg()
    {
        using var image = new Image<Rgba32>(64, 64);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder());
        Assert.Null(ImageUploadValidator.ValidateContent(ms.ToArray()));
    }

    [Fact]
    public void Content_RejectsTinyImage()
    {
        using var image = new Image<Rgba32>(8, 8);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        Assert.Equal(ImageUploadValidator.ErrorUnreadable, ImageUploadValidator.ValidateContent(ms.ToArray()));
    }

    [Fact]
    public async Task ValidateAsync_EndToEnd_AcceptsRealPngUpload()
    {
        using var image = new Image<Rgba32>(64, 64);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        ms.Position = 0;

        var file = new FormFileShim(ms.ToArray(), "photo.png", "image/png");
        var (bytes, ext, error) = await ImageUploadValidator.ValidateAsync(file);

        Assert.Null(error);
        Assert.NotNull(bytes);
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
