using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

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

            IImageFormat format;
            int width, height;
            string? software;
            try
            {
                using var image = Image.Load(bytes);
                format = image.Metadata.DecodedImageFormat
                    ?? throw new InvalidDataException("Unknown image format.");
                width = image.Width;
                height = image.Height;
                software = image.Metadata.ExifProfile?.Values
                    .FirstOrDefault(v => v.Tag == ExifTag.Software)
                    ?.GetValue()?.ToString();
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
            if (format.Name.Equals("GIF", StringComparison.OrdinalIgnoreCase))
            {
                return new ImageVerificationResult
                {
                    Status = "UnableToDetermine",
                    Confidence = 50,
                    Reason = "GIF images carry too little metadata for a meaningful assessment."
                };
            }

            // Signal 1: extension must match the actual detected format.
            var expected = format.FileExtensions.FirstOrDefault() ?? string.Empty;
            if (!string.IsNullOrEmpty(ext) && !format.FileExtensions.Contains(ext.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
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
            else if (format.Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase))
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
    }
}
