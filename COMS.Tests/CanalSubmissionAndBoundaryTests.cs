using COMS_MVC.Controllers;
using COMS_MVC.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Canal Create idempotency claim and the server-side Cebu polygon check
/// used by Canals/Sensors Create and Edit.
/// </summary>
public class CanalSubmissionAndBoundaryTests
{
    [Fact]
    public void Claim_is_one_time_per_key()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var token = Guid.NewGuid().ToString("N");

        Assert.True(CanalsController.TryClaimSubmission(cache, $"canal-submit:1:{token}"));
        Assert.False(CanalsController.TryClaimSubmission(cache, $"canal-submit:1:{token}"));
        Assert.True(CanalsController.TryClaimSubmission(cache, $"canal-submit:1:{Guid.NewGuid():N}"));
    }

    [Fact]
    public void Concurrent_claims_on_one_key_allow_exactly_one()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = $"canal-submit:1:{Guid.NewGuid():N}";
        var wins = 0;

        Parallel.For(0, 20, _ =>
        {
            if (CanalsController.TryClaimSubmission(cache, key))
            {
                Interlocked.Increment(ref wins);
            }
        });

        Assert.Equal(1, wins);
    }

    [Theory]
    [InlineData(10.3, 123.0, false)]       // sea west of Cebu
    [InlineData(10.3157, 123.8854, true)]  // Cebu City
    [InlineData(0.0, 0.0, false)]          // unset coordinates
    public void Polygon_check_matches_expected_points(double lat, double lng, bool expected)
    {
        Assert.Equal(expected, CreateBoundary().Contains(lat, lng));
    }

    internal static CebuBoundaryService CreateBoundary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "wwwroot", "data", "cebu-report-area.geojson")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var env = new FakeWebHostEnvironment
        {
            WebRootPath = Path.Combine(dir!.FullName, "wwwroot"),
            ContentRootPath = AppContext.BaseDirectory
        };
        return new CebuBoundaryService(env, NullLogger<CebuBoundaryService>.Instance);
    }
}
