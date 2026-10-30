using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace COMS_MVC.Services
{
    public interface ICebuBoundaryService
    {
        /// <summary>
        /// True when the point falls inside the permitted reporting area:
        /// the combined Cebu report-area polygon (Cebu Island plus Mactan
        /// Island — Lapu-Lapu City and Cordova — and Olango Island). When the
        /// polygon data file cannot be loaded the legacy rectangular bounds
        /// are used as a fallback so the report form never becomes unusable.
        /// </summary>
        bool Contains(double latitude, double longitude);
    }

    /// <summary>
    /// Loads wwwroot/data/cebu-report-area.geojson once and answers
    /// point-in-polygon queries for the resident "Report an Issue" map. The
    /// same GeoJSON file is fetched by the browser to draw the island mask
    /// and validate client side, so client and server always agree on where
    /// reports are allowed.
    /// </summary>
    public sealed class CebuBoundaryService : ICebuBoundaryService
    {
        // Legacy rectangular bounds (lat 9.30-11.45, lng 123.20-124.60). Kept
        // as (a) a fallback when the GeoJSON cannot be loaded and (b) the
        // cheap pre-filter before the polygon hit test.
        private const double FallbackMinLat = 9.30;
        private const double FallbackMaxLat = 11.45;
        private const double FallbackMinLng = 123.20;
        private const double FallbackMaxLng = 124.60;

        private readonly string? _dataPath;
        private readonly ILogger<CebuBoundaryService> _logger;
        private readonly object _loadLock = new();
        private List<double[][]>? _rings;   // each ring is [latitude, longitude] pairs
        private bool _loaded;

        public CebuBoundaryService(IWebHostEnvironment env, ILogger<CebuBoundaryService> logger)
        {
            _logger = logger;
            var webRoot = env.WebRootPath;
            if (string.IsNullOrEmpty(webRoot))
            {
                // Hosting environments without a physical wwwroot: fall back
                // to ContentRoot/wwwroot so the data file is still found
                // during local/test scenarios.
                webRoot = Path.Combine(env.ContentRootPath ?? ".", "wwwroot");
            }
            _dataPath = Path.Combine(webRoot, "data", "cebu-report-area.geojson");
        }

        public bool Contains(double latitude, double longitude)
        {
            EnsureLoaded();

            // Rectangular pre-filter (covers all included islands with margin).
            if (latitude < FallbackMinLat || latitude > FallbackMaxLat
                || longitude < FallbackMinLng || longitude > FallbackMaxLng)
            {
                return false;
            }

            if (_rings != null)
            {
                foreach (var ring in _rings)
                {
                    if (PointInRing(latitude, longitude, ring))
                    {
                        return true;
                    }
                }
                return false;
            }

            // _rings stays null only when the data file could not be used:
            // degrade to the legacy rectangle rather than blocking residents.
            return latitude >= FallbackMinLat && latitude <= FallbackMaxLat
                && longitude >= FallbackMinLng && longitude <= FallbackMaxLng;
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;
                try
                {
                    _rings = LoadRings(_dataPath!);
                    if (_rings == null || _rings.Count == 0)
                    {
                        _logger.LogError(
                            "No polygons found in {DataFile}; falling back to rectangular Cebu bounds.",
                            _dataPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to load Cebu boundary data from {DataFile}; falling back to rectangular Cebu bounds.",
                        _dataPath);
                }
                _loaded = true;
            }
        }

        /// <summary>Parses the GeoJSON and returns every outer coordinate ring ([lat, lng] pairs), or null.</summary>
        private static List<double[][]>? LoadRings(string path)
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            List<JsonElement>? geometries = null;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                geometries = new List<JsonElement> { root[0].GetProperty("geometry") };
            }
            else if (root.TryGetProperty("features", out var features) && features.GetArrayLength() > 0)
            {
                geometries = features.EnumerateArray()
                    .Select(f => f.GetProperty("geometry"))
                    .ToList();
            }
            else if (root.TryGetProperty("geometry", out var geo))
            {
                geometries = new List<JsonElement> { geo };
            }

            if (geometries == null)
            {
                return null;
            }

            var rings = new List<double[][]>();
            foreach (var geometry in geometries)
            {
                var type = geometry.GetProperty("type").GetString();
                if (type == "MultiPolygon")
                {
                    foreach (var polygon in geometry.GetProperty("coordinates").EnumerateArray())
                    {
                        rings.Add(ConvertRing(polygon[0]));
                    }
                }
                else if (type == "Polygon")
                {
                    rings.Add(ConvertRing(geometry.GetProperty("coordinates")[0]));
                }
            }
            return rings;
        }

        private static double[][] ConvertRing(JsonElement ring)
        {
            var points = new List<double[]>();
            foreach (var p in ring.EnumerateArray())
            {
                // GeoJSON order is [longitude, latitude]; store [lat, lng].
                points.Add(new[] { p[1].GetDouble(), p[0].GetDouble() });
            }
            return points.ToArray();
        }

        /// <summary>Even-odd ray casting over a closed lat/lng ring.</summary>
        private static bool PointInRing(double lat, double lng, double[][] ring)
        {
            var inside = false;
            var j = ring.Length - 1;
            for (var i = 0; i < ring.Length; i++)
            {
                var latI = ring[i][0];
                var lngI = ring[i][1];
                var latJ = ring[j][0];
                var lngJ = ring[j][1];
                var intersects = (latI > lat) != (latJ > lat)
                    && lng < (lngJ - lngI) * (lat - latI) / (latJ - latI) + lngI;
                if (intersects)
                {
                    inside = !inside;
                }
                j = i;
            }
            return inside;
        }
    }
}
