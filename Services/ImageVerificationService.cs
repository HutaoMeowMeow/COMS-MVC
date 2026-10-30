using Microsoft.Extensions.Options;
using SkiaSharp;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Server-side only. Never exposed to the frontend; the key (for future
    /// external providers) stays in env vars / user-secrets.
    /// </summary>
    public sealed class ImageVerificationOptions
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Active analyzer. "Heuristic" runs fully offline.</summary>
        public string Provider { get; set; } = "Heuristic";
        public string ApiKey { get; set; } = string.Empty;
    }

    public sealed class ImageVerificationResult
    {
        /// <summary>Authentic / Suspicious / LikelyManipulated / UnableToDetermine / Failed.</summary>
        public string Status { get; set; } = "UnableToDetermine";
        public int? Confidence { get; set; }
        public string? Reason { get; set; }
    }

    public interface IImageVerificationService
    {
        bool IsEnabled { get; }
        /// <summary>Decode-validates the bytes and returns an authenticity assessment. Never throws.</summary>
        Task<ImageVerificationResult> AnalyzeAsync(byte[] bytes, string fileName, CancellationToken ct = default);
    }

    /// <summary>
    /// Offline heuristic analyzer. Scores independent, explainable signals —
    /// generator/editor metadata, format/extension mismatch, stripped metadata,
    /// dimension anomalies — into an advisory result. It never claims certainty:
    /// top confidence is capped, and anything undecodable yields
    /// UnableToDetermine/Failed instead of a verdict.
    /// </summary>
    public sealed class HeuristicImageVerificationService : IImageVerificationService
    {
        private readonly ImageVerificationOptions _options;
        private readonly ILogger<HeuristicImageVerificationService> _logger;

        private static readonly string[] AiGeneratorMarkers =
        {
            "stable diffusion", "automatic1111", "comfyui", "midjourney", "dall",
            "firefly", "leonardo", "novelai", "civitai", "ideogram", "dalle",
            "generative fill", "ai generated", "ai-generated"
        };

        private static readonly string[] EditorMarkers =
        {
            "photoshop", "lightroom", "gimp", "snapseed", "picsart", "canva",
            "facetune", "meitu", "pixlr", "fotor", "paint.net", "capture one"
        };

        public HeuristicImageVerificationService(
            IOptions<ImageVerificationOptions> options,
            ILogger<HeuristicImageVerificationService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public bool IsEnabled => _options.Enabled;

        public async Task<ImageVerificationResult> AnalyzeAsync(byte[] bytes, string fileName, CancellationToken ct = default)
        {
            try
            {
                return await Task.Run(() => Analyze(bytes, fileName), ct);
            }
            catch (OperationCanceledException)
            {
                return new ImageVerificationResult { Status = "Failed", Reason = "Analysis was cancelled." };
            }
            catch (Exception ex)
            {
                // Advisory feature: never leak details, never throw to the caller.
                _logger.LogError(ex, "Image verification failed.");
                return new ImageVerificationResult { Status = "Failed", Reason = "Analysis could not be completed." };
            }
        }

        private ImageVerificationResult Analyze(byte[] bytes, string fileName)
        {
            if (bytes.Length == 0)
            {
                return new ImageVerificationResult { Status = "Failed", Reason = "Empty image file." };
            }

            string format;
            int width, height;
            string? software;
            try
            {
                // Same bounded check as the upload gate: a declared-oversized
                // bomb is rejected before any bitmap allocation.
                if (ImageUploadValidator.ExceedsPixelBudget(bytes, out _, out _))
                {
                    return new ImageVerificationResult
                    {
                        Status = "UnableToDetermine",
                        Reason = "Image dimensions exceed supported limits."
                    };
                }

                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null)
                {
                    return new ImageVerificationResult
                    {
                        Status = "UnableToDetermine",
                        Reason = "Image data could not be decoded for analysis."
                    };
                }
                format = DetectFormat(bytes);
                width = bitmap.Width;
                height = bitmap.Height;
                software = ExifReader.GetSoftware(bytes);
            }
            catch
            {
                return new ImageVerificationResult
                {
                    Status = "UnableToDetermine",
                    Reason = "Image data could not be decoded for analysis."
                };
            }

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var score = 0;
            var notes = new List<string>();

            // GIFs carry almost no metadata — be honest about limits.
            if (format.Equals("GIF", StringComparison.OrdinalIgnoreCase))
            {
                return new ImageVerificationResult
                {
                    Status = "UnableToDetermine",
                    Confidence = 50,
                    Reason = "GIF images carry too little metadata for a meaningful assessment."
                };
            }

            // Signal 1: extension must match the actual detected format.
            var expected = format switch
            {
                "JPEG" => "jpg",
                "PNG" => "png",
                "GIF" => "gif",
                _ => string.Empty
            };
            if (!string.IsNullOrEmpty(ext) && !string.Equals(ext.TrimStart('.'), expected, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
                notes.Add($"File extension ({ext}) does not match detected format ({expected}).");
            }

            // Signal 2: software/generator metadata.
            if (!string.IsNullOrWhiteSpace(software))
            {
                var sw = software.ToLowerInvariant();
                if (AiGeneratorMarkers.Any(m => sw.Contains(m)))
                {
                    return new ImageVerificationResult
                    {
                        Status = "LikelyManipulated",
                        Confidence = 88,
                        Reason = "Image metadata indicates AI-generation software."
                    };
                }
                if (EditorMarkers.Any(m => sw.Contains(m)))
                {
                    score += 25;
                    notes.Add("Image was processed with photo-editing software.");
                }
            }
            else if (format.Equals("JPEG", StringComparison.OrdinalIgnoreCase))
            {
                // Stripped metadata is common (screenshots, downloads) — weak signal only.
                score += 10;
                notes.Add("No camera metadata present (common for screenshots or downloads).");
            }

            // Signal 3: dimension anomalies.
            if (width < 320 || height < 320)
            {
                score += 10;
                notes.Add("Very small image dimensions.");
            }
            var aspect = (double)Math.Max(width, height) / Math.Max(1, Math.Min(width, height));
            if (aspect > 4)
            {
                score += 10;
                notes.Add("Unusual panoramic aspect ratio.");
            }

            if (score >= 55)
            {
                return new ImageVerificationResult
                {
                    Status = "LikelyManipulated",
                    Confidence = Math.Min(80, 55 + score / 2),
                    Reason = string.Join(" ", notes)
                };
            }
            if (score >= 25)
            {
                return new ImageVerificationResult
                {
                    Status = "Suspicious",
                    Confidence = Math.Min(75, 45 + score / 2),
                    Reason = string.Join(" ", notes)
                };
            }
            return new ImageVerificationResult
            {
                Status = "Authentic",
                Confidence = notes.Count == 0 ? 85 : 65,
                Reason = notes.Count == 0
                    ? "No significant manipulation indicators detected."
                    : string.Join(" ", notes)
            };
        }

        private static string DetectFormat(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "JPEG";
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "PNG";
            if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                return "GIF";
            return "Unknown";
        }
    }

    /// <summary>
    /// Minimal EXIF reader: extracts the Software tag (0x0131) from JPEG EXIF
    /// APP1 segments. Returns null when no EXIF or no Software tag is present.
    /// </summary>
    public static class ExifReader
    {
        private const ushort TagSoftware = 0x0131;

        public static string? GetSoftware(byte[] bytes)
        {
            // EXIF lives in a JPEG APP1 segment: 0xFF 0xE1, length, "Exif\0\0", then TIFF.
            var app1 = FindApp1Segment(bytes);
            if (app1 == null) return null;

            var data = app1;
            // Skip "Exif\0\0" (6 bytes) to reach the TIFF header.
            var tiffStart = 6;
            if (data.Length < tiffStart + 8) return null;

            // Byte order: "II" = little-endian, "MM" = big-endian.
            var littleEndian = data[tiffStart] == 0x49 && data[tiffStart + 1] == 0x49;
            if (!littleEndian && !(data[tiffStart] == 0x4D && data[tiffStart + 1] == 0x4D))
                return null;

            // Magic number 42.
            var magic = ReadUInt16(data, tiffStart + 2, littleEndian);
            if (magic != 42) return null;

            var ifd0Offset = (int)ReadUInt32(data, tiffStart + 4, littleEndian);
            var ifd0Abs = tiffStart + ifd0Offset;
            if (ifd0Abs + 2 > data.Length) return null;

            var entryCount = ReadUInt16(data, ifd0Abs, littleEndian);
            for (var i = 0; i < entryCount; i++)
            {
                var entryOffset = ifd0Abs + 2 + i * 12;
                if (entryOffset + 12 > data.Length) break;
                var tag = ReadUInt16(data, entryOffset, littleEndian);
                if (tag != TagSoftware) continue;
                // Type 2 = ASCII string.
                var type = ReadUInt16(data, entryOffset + 2, littleEndian);
                var count = ReadUInt32(data, entryOffset + 4, littleEndian);
                var valueOffset = (int)ReadUInt32(data, entryOffset + 8, littleEndian);
                if (type != 2) continue;

                var strStart = tiffStart + valueOffset;
                if (strStart >= data.Length) continue;
                var len = (int)Math.Min(count, data.Length - strStart);
                var sb = new System.Text.StringBuilder(len);
                for (var j = 0; j < len; j++)
                {
                    var b = data[strStart + j];
                    if (b == 0) break;
                    sb.Append((char)b);
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }
            return null;
        }

        private static byte[]? FindApp1Segment(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length - 1; i++)
            {
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xE1)
                {
                    if (i + 4 > bytes.Length) return null;
                    var segLen = ReadUInt16(bytes, i + 2, littleEndian: false);
                    var segStart = i + 4;
                    var segEnd = Math.Min(segStart + segLen - 2, bytes.Length);
                    var len = segEnd - segStart;
                    var seg = new byte[len];
                    Array.Copy(bytes, segStart, seg, 0, len);
                    return seg;
                }
            }
            return null;
        }

        private static ushort ReadUInt16(byte[] data, int offset, bool littleEndian)
        {
            if (littleEndian)
                return (ushort)(data[offset] | (data[offset + 1] << 8));
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
        {
            if (littleEndian)
                return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        }
    }
}
