using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace COMS_MVC.Services
{
    /// <summary>Server-side only. Binds from appsettings.json -&gt; "ImageRelevance".</summary>
    public sealed class ImageRelevanceOptions
    {
        public bool Enabled { get; set; } = true;
    }

    public sealed class ImageRelevanceResult
    {
        /// <summary>Relevant / Irrelevant / Uncertain.</summary>
        public string Status { get; set; } = "Uncertain";
        public int? Confidence { get; set; }
        public string? Reason { get; set; }
        /// <summary>
        /// Best-matching report category when the evidence supports one
        /// ("Garbage / Debris", "Blocked Canal", "Flooding", "Damaged Canal",
        /// "Unusual Water Level"). Null when no category can be supported.
        /// </summary>
        public string? SuggestedCategory { get; set; }
    }

    public interface IImageRelevanceService
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Judges whether the photo shows a canal obstruction, drainage
        /// problem, flooding, damaged canal, or unusual water level.
        /// Pure pixel analysis, never throws. Advisory honesty rules: ambiguous
        /// images yield Uncertain (never silent approval), reasons describe
        /// only what is visibly supported, confidence is capped.
        /// </summary>
        Task<ImageRelevanceResult> AnalyzeAsync(byte[] bytes, CancellationToken ct = default);
    }

    /// <summary>
    /// Offline pixel-heuristic relevance analyzer (SkiaSharp only, no model
    /// download, no external service). It measures observable image properties
    /// — quality (blur/darkness), synthetic-graphics traits, water/murk hues,
    /// debris texture, concrete-structure presence — and combines them with a
    /// conservative rule set that prefers Uncertain over guessing.
    ///
    /// Documented limits: this is NOT object recognition. It cannot read text,
    /// identify people, or prove a clean canal is obstructed. Thresholds were
    /// tuned on synthetic scenes and must be re-validated on real photos;
    /// when in doubt it returns Uncertain so staff review decides.
    /// </summary>
    public sealed class ImageRelevanceService : IImageRelevanceService
    {
        private readonly ImageRelevanceOptions _options;
        private readonly ILogger<ImageRelevanceService> _logger;

        // ---- Tuned thresholds (tuning data in the reltest harness) ----
        private const double DarkFractionLimit = 0.55;   // frac. pixels w/ luminance < 30
        private const double MeanLuminanceFloor = 28.0;
        private const double BlurVarianceFloor = 5.0;    // Laplacian variance on 0-255 gray
        private const double GraphicsFlatFloor = 0.50;
        private const double GraphicsTextureCeiling = 6.0;
        private const double GraphicsEdgeFloor = 0.006;  // text strokes on flat fills
        private const double PoolBlueFloor = 0.45;       // vivid-blue fraction
        private const double PoolTextureCeiling = 9.0;
        private const int MaxConfidence = 80;

        public ImageRelevanceService(
            IOptions<ImageRelevanceOptions>? options = null,
            ILogger<ImageRelevanceService>? logger = null)
        {
            _options = options?.Value ?? new ImageRelevanceOptions();
            _logger = logger ?? NullLogger<ImageRelevanceService>.Instance;
        }

        public bool IsEnabled => _options.Enabled;

        public Task<ImageRelevanceResult> AnalyzeAsync(byte[] bytes, CancellationToken ct = default)
        {
            try
            {
                return Task.FromResult(Analyze(bytes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Image relevance analysis failed.");
                return Task.FromResult(new ImageRelevanceResult
                {
                    Status = "Uncertain",
                    Reason = "The photo could not be analyzed. Please upload a clearer photo."
                });
            }
        }

        /// <summary>Diagnostic feature vector (also used to tune thresholds).</summary>
        public sealed class Features
        {
            public double MeanLuminance;
            public double DarkFraction;
            public double BlurVariance;
            public double UniqueRatio;
            public double FlatFraction;
            public double WaterFraction;    // blue/cyan/green + murky-brown water
            public double BlueGreenFraction; // blue/cyan/green water only (excludes murk)
            public double VividBlueFraction; // high-saturation blue (pool/sky-like)
            public double MurkFraction;     // low-saturation brown/gray mid-dark (floodwater/silt)
            public double DebrisTexture;    // mean local abs-diff (clutter/ripples/rubble)
            public double StructureFraction; // low-saturation gray (concrete/masonry)
            public double EdgeDensity;      // strong-gradient fraction (cracks/structure)
            public double SkinFraction;     // informational only; never vetoes
        }

        public static Features Extract(SKBitmap bitmap)
        {
            // Stride-sampled grid: bounded work, roughly size-invariant.
            const int budget = 40000;
            var stride = Math.Max(1, (int)Math.Sqrt((double)bitmap.Width * bitmap.Height / budget));
            var w = Math.Max(8, bitmap.Width / stride);
            var h = Math.Max(8, bitmap.Height / stride);

            var lum = new double[w * h];
            var n = w * h;
            var dark = 0;
            var lumSum = 0.0;
            var colorCounts = new Dictionary<int, int>();
            var water = 0; var vividBlue = 0; var murk = 0; var structure = 0; var skin = 0; var blueGreen = 0;

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var px = bitmap.GetPixel(x * stride, y * stride);
                    double r = px.Red, g = px.Green, b = px.Blue;
                    var l = 0.299 * r + 0.587 * g + 0.114 * b;
                    lum[y * w + x] = l;
                    lumSum += l;
                    if (l < 30) dark++;

                    // 12-bit quantized color for the graphics test.
                    var key = ((r > 240 ? 15 : (int)(r / 16)) << 8)
                            | ((g > 240 ? 15 : (int)(g / 16)) << 4)
                            | (b > 240 ? 15 : (int)(b / 16));
                    colorCounts[key] = colorCounts.TryGetValue(key, out var c) ? c + 1 : 1;

                    var mx = Math.Max(r, Math.Max(g, b));
                    var mn = Math.Min(r, Math.Min(g, b));
                    var sat = mx <= 0 ? 0 : (mx - mn) / mx; // 0..1
                    var hueBlue = b >= r && b >= g;
                    var hueGreen = g >= r && g >= b;
                    var hueBrown = r >= g && g >= b;

                    if ((hueBlue || hueGreen) && sat > 0.25 && l > 40) { water++; blueGreen++; }
                    if (hueBlue && sat > 0.55 && l > 70) vividBlue++;
                    // Murk = brownish wet silt/mud (floodwater, sediment). Narrow band
                    // on purpose: gray concrete and skin tones must NOT count.
                    if (hueBrown && sat < 0.40 && l >= 35 && l <= 130) murk++;
                    // Murky-brown water also counts as water (floodwater is
                    // rarely blue); tracked separately from blue/green below.
                    if (hueBrown && sat > 0.20 && l > 40) water++;
                    if (sat < 0.22 && l >= 60 && l <= 200) structure++;
                    // Common skin-tone band (informational only).
                    if (r > 150 && g > 100 && g < 200 && b > 70 && b < 170 && r > g && g > b
                        && (r - b) is > 30 and < 130) skin++;
                }
            }

            // Flat runs: full-resolution horizontal runs of identical quantized
            // color (len >= 12) sampled on every 8th row — catches flat vector
            // fills and text bars of screenshots/memes/documents.
            var flat = 0;
            var flatN = 0;
            for (var y = 0; y < bitmap.Height; y += 8)
            {
                var run = 1;
                var prev = -1;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var px = bitmap.GetPixel(x, y);
                    var key = (((int)px.Red / 16) << 8) | (((int)px.Green / 16) << 4) | ((int)px.Blue / 16);
                    if (key == prev) run++;
                    else
                    {
                        if (run >= 12) flat += run;
                        run = 1;
                        prev = key;
                    }
                    flatN++;
                }
                if (run >= 12) flat += run;
            }

            // Blur: variance of the Laplacian on grayscale.
            double lapSum = 0, lapSq = 0;
            var lapN = 0;
            for (var y = 1; y < h - 1; y++)
            {
                for (var x = 1; x < w - 1; x++)
                {
                    var v = lum[y * w + x] * 4 - lum[y * w + x - 1] - lum[y * w + x + 1]
                          - lum[(y - 1) * w + x] - lum[(y + 1) * w + x];
                    lapSum += v; lapSq += v * v; lapN++;
                }
            }
            var lapMean = lapN == 0 ? 0 : lapSum / lapN;
            var blurVar = lapN == 0 ? 0 : Math.Max(0, lapSq / lapN - lapMean * lapMean);

            // Texture + edges: mean abs-diff to right/down neighbours; strong edges.
            double texSum = 0; var texN = 0; var strong = 0;
            for (var y = 0; y < h - 1; y++)
            {
                for (var x = 0; x < w - 1; x++)
                {
                    var d = (Math.Abs(lum[y * w + x] - lum[y * w + x + 1])
                           + Math.Abs(lum[y * w + x] - lum[(y + 1) * w + x])) / 2.0;
                    texSum += d; texN++;
                    if (d > 45) strong++;
                }
            }

            return new Features
            {
                MeanLuminance = lumSum / n,
                DarkFraction = (double)dark / n,
                BlurVariance = blurVar,
                UniqueRatio = (double)colorCounts.Count / n,
                FlatFraction = flatN == 0 ? 0 : (double)flat / flatN,
                WaterFraction = (double)water / n,
                BlueGreenFraction = (double)blueGreen / n,
                VividBlueFraction = (double)vividBlue / n,
                MurkFraction = (double)murk / n,
                DebrisTexture = texN == 0 ? 0 : texSum / texN,
                StructureFraction = (double)structure / n,
                EdgeDensity = texN == 0 ? 0 : (double)strong / texN,
                SkinFraction = (double)skin / n
            };
        }

        private ImageRelevanceResult Analyze(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return Uncertain("The photo could not be read. Please upload a clearer photo.");

            SKBitmap bitmap;
            try
            {
                bitmap = SKBitmap.Decode(bytes);
            }
            catch
            {
                return Uncertain("The photo could not be decoded. Please upload a clearer photo.");
            }
            if (bitmap == null)
                return Uncertain("The photo could not be decoded. Please upload a clearer photo.");

            using (bitmap)
            {
                if (bitmap.Width < 64 || bitmap.Height < 64)
                    return Uncertain("The photo is too small to assess. Please upload a clearer photo.");

                var f = Extract(bitmap);

                // 1. Quality gate: never guess on unreadable input.
                if (f.DarkFraction > DarkFractionLimit || f.MeanLuminance < MeanLuminanceFloor)
                    return Uncertain("The photo is too dark to assess. Please upload a brighter, clearer photo.");
                if (f.BlurVariance < BlurVarianceFloor)
                    return Uncertain("The photo is too blurry to assess. Please upload a sharper photo.");

                var water = f.WaterFraction;
                var murk = f.MurkFraction;
                var debris = f.DebrisTexture;
                var structure = f.StructureFraction;

                // 2. Synthetic graphics (screenshots, memes, documents, logos):
                //    flat fills plus crisp text-like edges, without the broad
                //    concrete-gray masses of real outdoor scenes.
                if (f.FlatFraction > GraphicsFlatFloor && debris < GraphicsTextureCeiling
                    && f.EdgeDensity > GraphicsEdgeFloor && water < 0.30 && structure < 0.30)
                    return Reject("The image looks like a screenshot, meme, document, or graphic rather than a photo of a canal, drainage, or flood condition.");

                // 3. Smooth images with no water evidence (portraits, illustrations,
                //    plain objects): not canal evidence. A person IN a canal/flood
                //    photo does not trigger this because that photo has water.
                if (f.FlatFraction > 0.50 && debris < 4.0 && f.EdgeDensity < 0.008 && water < 0.25)
                    return Reject("This image does not appear to show a canal obstruction, drainage issue, flooding, or another condition monitored by COMS. Please upload a relevant image.");

                // 4. Vivid uniform blue with no murk/texture: pools, aquariums,
                //    clear skies — water without flood evidence.
                if (f.VividBlueFraction > PoolBlueFloor && debris < PoolTextureCeiling && murk < 0.08)
                    return Reject("The image shows clear blue water without visible flood, debris, or drainage conditions.");

                // 5. Open water without corroboration (sea, lake, calm river):
                //    no murk, no texture, no structure — nothing to assess.
                if (water > 0.25 && debris < 5.0 && murk < 0.08 && f.EdgeDensity < 0.01 && structure < 0.10)
                    return Reject("The image shows open water without visible flood, debris, or drainage conditions.");

                // 6. No COMS evidence at all (food, animals, isolated objects).
                if (water < 0.04 && murk < 0.10 && debris < 7.0 && structure < 0.10 && f.EdgeDensity < 0.03)
                    return Reject("This image does not appear to show a canal obstruction, drainage issue, flooding, or another condition monitored by COMS. Please upload a relevant image.");

                // 7. Strong corroborated combos -> Relevant + suggested category.
                // Blocked canal: water + structure + heavy clutter.
                if (water > 0.05 && structure > 0.12 && debris > 8.0)
                    return Accept("The photo shows water in a built structure with heavy accumulated material consistent with a blocked canal or drain.", "Blocked Canal",
                        45 + (int)(30 * Math.Min(1, (debris - 8.0) / 8.0)));
                // Flooding: extensive murky water with ripple/texture witness plus
                // a second witness (blue-green water, structures, dense edges,
                // or a vast expanse) so bare dirt and smooth darkened photos
                // do not qualify.
                if (murk > 0.32 && water > 0.10 && debris > 3.5
                    && (f.BlueGreenFraction > 0.05 || structure > 0.08 || f.EdgeDensity > 0.01 || water > 0.60))
                    return Accept("The photo shows extensive murky floodwater consistent with flooding.", "Flooding",
                        45 + (int)(30 * Math.Min(1, (murk - 0.32) * 3)));
                // Garbage/debris: water + heavy clutter.
                if (water > 0.06 && debris > 9.0)
                    return Accept("The photo shows waste-like clutter in or near water consistent with garbage or debris.", "Garbage / Debris",
                        45 + (int)(30 * Math.Min(1, (debris - 9.0) / 8.0)));
                // Damaged canal: concrete structure + dense cracking edges + water.
                if (structure > 0.15 && f.EdgeDensity > 0.008 && water > 0.04)
                    return Accept("The photo shows a concrete structure with dense cracking and water consistent with a damaged canal.", "Damaged Canal",
                        45 + (int)(30 * Math.Min(1, (f.EdgeDensity - 0.008) * 40)));
                // Unusual water level: very extensive water with murk. A single
                // photo cannot prove the normal baseline, so this is reported
                // as flooding with that caveat, never as a measured level.
                if (water > 0.35 && murk > 0.15 && debris > 3.5
                    && (f.BlueGreenFraction > 0.05 || structure > 0.08 || f.EdgeDensity > 0.01))
                    return Accept("The photo shows very extensive murky water consistent with flooding or an unusually high water level.", "Flooding",
                        45 + (int)(30 * Math.Min(1, (water - 0.35) * 2)));

                // 8. Water or structure visible but nothing corroborates an
                //    obstruction, damage, or flood (e.g. a clean canal): do NOT
                //    claim a blockage.
                if (water > 0.04 || murk > 0.10 || structure > 0.10)
                    return Uncertain("The photo shows a waterway but does not provide enough evidence to confirm an obstruction, damage, or flood condition. Please upload a photo that clearly shows the problem.");

                return Uncertain("The photo could not be reliably assessed. Please upload a clearer photo.");
            }
        }

        private static ImageRelevanceResult Accept(string reason, string category, int confidence) =>
            new() { Status = "Relevant", Confidence = Math.Min(MaxConfidence, confidence), Reason = reason, SuggestedCategory = category };

        private static ImageRelevanceResult Reject(string reason) =>
            new() { Status = "Irrelevant", Confidence = 70, Reason = reason };

        private static ImageRelevanceResult Uncertain(string reason) =>
            new() { Status = "Uncertain", Confidence = null, Reason = reason };
    }
}
