using COMS_MVC.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Boundary data for the resident report map: the combined report-area
/// polygons (wwwroot/data/cebu-report-area.geojson) must accept points on
/// Cebu Island AND on Mactan Island (Lapu-Lapu City, Cordova) and Olango
/// Island (Lapu-Lapu City barangays), while rejecting Bantayan, Camotes,
/// Bohol, Negros, Leyte and open water.
/// </summary>
public class CebuBoundaryTests
{
    private static string RepoWwwroot()
    {
        // Tests run from COMS.Tests/bin/Debug/net8.0; walk up to the repo
        // root that contains wwwroot/data/cebu-report-area.geojson.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot", "data", "cebu-report-area.geojson");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "wwwroot");
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate wwwroot/data/cebu-report-area.geojson from test output.");
    }

    private static COMS_MVC.Services.CebuBoundaryService Create(string? wwwroot = null)
    {
        var env = new FakeWebHostEnvironment
        {
            WebRootPath = wwwroot ?? RepoWwwroot(),
            ContentRootPath = AppContext.BaseDirectory
        };
        return new COMS_MVC.Services.CebuBoundaryService(env, NullLogger<COMS_MVC.Services.CebuBoundaryService>.Instance);
    }

    public static TheoryData<string, double, double, bool> BoundaryCases => new()
    {
        // name, latitude, longitude, expected (true = inside report area)
        { "Cebu City Hall", 10.2926, 123.9031, true },
        { "Cebu Capitol", 10.3160, 123.8818, true },
        { "Mandaue city hall", 10.3239, 123.9347, true },
        { "Toledo city hall", 10.3777, 123.6390, true },
        { "Bogo city hall", 11.1250, 123.9770, true },
        { "Danao city hall", 10.5270, 123.8460, true },
        { "Carcar", 10.1060, 123.6410, true },
        { "Argao", 9.9613, 123.5897, true },
        { "Moalboal", 9.9372, 123.3922, true },
        { "Badian", 9.8701, 123.3990, true },
        { "Medellin", 11.1284, 123.9616, true },
        { "Santander (south tip)", 9.4171, 123.3352, true },
        { "Talisay city hall", 10.2441, 123.8460, true },
        { "Lapu-Lapu city hall (Mactan)", 10.3102, 123.9733, true },
        { "Mactan (Pusok, Lapu-Lapu)", 10.3188, 123.9720, true },
        { "Mactan (airport area)", 10.3050, 124.0000, true },
        { "Cordova, Mactan", 10.2522, 123.9495, true },
        { "Olango Island (Santa Rosa, Lapu-Lapu)", 10.2629, 124.0514, true },
        { "Olango Island (east side)", 10.2720, 124.0620, true },
        { "Poro (Camotes)", 10.6800, 124.4000, false },
        { "Tagbilaran (Bohol)", 9.6490, 123.8520, false },
        { "Sagbayan (Bohol)", 10.1900, 123.8600, false },
        { "San Carlos (Negros)", 10.4220, 123.4290, false },
        { "Sibulan (Negros)", 9.3600, 123.2900, false },
        { "Santa Fe (Bantayan)", 11.1800, 123.7500, false },
        { "Mactan-Olango channel (open water)", 10.2700, 124.0100, false },
        { "Cebu strait open sea", 10.1500, 123.9000, false },
        { "East of Olango (Bohol sea)", 10.2700, 124.0900, false },
        { "Far north (outside island bounds)", 12.5000, 123.9000, false },
        { "Far outside everything", 20.0000, 30.0000, false },
    };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Contains_matches_island_reality(string name, double lat, double lng, bool expected)
    {
        var svc = Create();
        Assert.True(svc.Contains(lat, lng) == expected,
            $"Point '{name}' ({lat},{lng}) expected inside={expected}.");
    }

    [Fact]
    public void Polygon_covers_mactan_points_within_legacy_rectangle()
    {
        // Regression guard: the legacy rectangular bounds (lat 9.30-11.45,
        // lng 123.20-124.60) accepted Mactan/Olango points, but the original
        // island-only polygon rejected them. The report-area polygons must
        // keep accepting Lapu-Lapu City locations.
        var svc = Create();
        var mustStayAccepted = new[]
        {
            (10.3102, 123.9733), // Lapu-Lapu City Hall, Mactan
            (10.3050, 124.0000), // Mactan airport area
            (10.2522, 123.9495), // Cordova, Mactan
            (10.2629, 124.0514), // Santa Rosa, Olango Island
        };
        foreach (var (lat, lng) in mustStayAccepted)
        {
            var insideLegacy = lat >= 9.30 && lat <= 11.45 && lng >= 123.20 && lng <= 124.60;
            Assert.True(insideLegacy, $"Sanity: ({lat},{lng}) should be inside the legacy rectangle.");
            Assert.True(svc.Contains(lat, lng),
                $"({lat},{lng}) is a valid Lapu-Lapu City location and must be accepted.");
        }
    }

    [Fact]
    public void Points_in_legacy_rectangle_still_rejected_by_polygons()
    {
        // The polygons must still reject non-permitted points that the legacy
        // rectangle wrongly accepted (Bantayan, Camotes, open channels).
        var svc = Create();
        var mustStayRejected = new[]
        {
            (11.1800, 123.7500), // Santa Fe, Bantayan
            (10.6800, 124.4000), // Poro, Camotes
            (10.2700, 124.0100), // Mactan-Olango channel (open water)
            (10.2700, 124.0900), // Bohol sea east of Olango
        };
        foreach (var (lat, lng) in mustStayRejected)
        {
            var insideLegacy = lat >= 9.30 && lat <= 11.45 && lng >= 123.20 && lng <= 124.60;
            Assert.True(insideLegacy, $"Sanity: ({lat},{lng}) should be inside the legacy rectangle.");
            Assert.False(svc.Contains(lat, lng),
                $"({lat},{lng}) is outside the permitted reporting area and must be rejected.");
        }
    }

    [Fact]
    public void Boundary_data_file_exists_with_all_island_rings()
    {
        var path = Path.Combine(RepoWwwroot(), "data", "cebu-report-area.geojson");
        Assert.True(new FileInfo(path).Length > 10000, "Polygon data seems missing or truncated.");
        var json = File.ReadAllText(path);
        Assert.Contains("\"Cebu Island\"", json);
        Assert.Contains("\"Mactan Island\"", json);
        Assert.Contains("\"Olango Island\"", json);
        Assert.Contains("Polygon", json);
    }

    [Fact]
    public void Missing_data_file_falls_back_to_rectangular_bounds()
    {
        // Non-existent wwwroot: service degrades to the legacy rectangle so
        // report submission is never silently broken by a bad deploy.
        var svc = Create(Path.Combine(Path.GetTempPath(), "no-such-wwwroot-" + Guid.NewGuid().ToString("N")));
        Assert.True(svc.Contains(10.0, 123.5));     // inside the legacy rectangle
        Assert.False(svc.Contains(20.0, 30.0));     // outside it
    }
}
