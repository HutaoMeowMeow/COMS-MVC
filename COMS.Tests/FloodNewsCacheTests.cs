using System.Net;
using COMS_MVC.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Regression tests for the FloodNews cache-aliasing fix (fix #8): stale,
/// throttled, and error responses must be copies — never mutations of the
/// shared cached object.
/// </summary>
public sealed class FloodNewsCacheTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private sealed class SwitchableHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Responder(request));
    }

    private static string Rss(string title, string body)
    {
        // NOTE: no leading whitespace — an XML declaration must be first.
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<rss version=\"2.0\"><channel><title>Test Feed</title>"
            + "<item><title>" + title + "</title>"
            + "<link>https://example.com/flood-cebu-1</link>"
            + "<description>" + body + "</description>"
            + "<pubDate>" + DateTimeOffset.UtcNow.ToString("R") + "</pubDate>"
            + "<category>Cebu</category></item>"
            + "</channel></rss>";
    }

    private FloodNewsService Service(SwitchableHandler handler, bool enabled = true)
        => Service(handler, enabled, new List<string> { "https://example.com/ph.xml" });

    private FloodNewsService Service(
        SwitchableHandler handler, bool enabled, List<string> feeds)
    {
        var options = Options.Create(new FloodNewsOptions
        {
            Enabled = enabled,
            GdacsFeedUrl = "https://example.com/gdacs.xml",
            PhilippineNewsFeeds = feeds,
            CacheMinutes = 20,
            MaxArticles = 30,
            MaxAgeDays = 7,
            HttpTimeoutSeconds = 10
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return new FloodNewsService(http, options, _cache, NullLogger<FloodNewsService>.Instance);
    }

    private static void RespondSuccess(SwitchableHandler handler)
    {
        handler.Responder = req =>
        {
            // GDACS requires structured metadata — return empty for it.
            if (req.RequestUri!.AbsoluteUri.Contains("gdacs"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        @"<?xml version=""1.0""?><rss version=""2.0""><channel><title>GDACS</title></channel></rss>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Rss(
                    "Cebu flood hits riverside communities",
                    "Major flooding displaced families in Cebu City. Rescue teams deployed to the affected areas."))
            };
        };
    }

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task Refresh_Throttled_ReturnsCopy_NotSameReference()
    {
        var handler = new SwitchableHandler();
        RespondSuccess(handler);
        var svc = Service(handler);

        var first = await svc.RefreshAsync();
        Assert.False(first.RefreshThrottled);
        Assert.NotEmpty(first.Articles);

        var throttled = await svc.RefreshAsync();
        Assert.True(throttled.RefreshThrottled);
        Assert.False(ReferenceEquals(first, throttled));
        Assert.Equal(first.Articles.Count, throttled.Articles.Count);

        // Mutating the throttled copy must not corrupt the cache.
        throttled.Articles.Clear();
        var again = await svc.GetLatestAsync();
        Assert.NotEmpty(again.Articles);
    }

    [Fact]
    public async Task Failure_WithLastGood_ReturnsStaleCopy_CacheUnmutated()
    {
        var handler = new SwitchableHandler();
        RespondSuccess(handler);
        var svc = Service(handler);
        var good = await svc.RefreshAsync();
        Assert.NotEmpty(good.Articles);

        // Evict the read cache but keep last-good; now fail every provider.
        _cache.Remove("flood-news-v4");
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        // Bypass the 2-minute force cooldown with a fresh service on the same cache.
        var failing = Service(handler);
        _cache.Remove("flood-news-v4:force-at");
        var stale = await failing.RefreshAsync();

        Assert.True(stale.IsStale);
        Assert.NotEmpty(stale.Articles);

        // The stale response must be a copy: mutating it leaves last-good intact.
        var before = stale.Articles.Count;
        stale.Articles.Clear();
        _cache.Remove("flood-news-v4");
        _cache.Remove("flood-news-v4:force-at");
        var stale2 = await failing.RefreshAsync();
        Assert.Equal(before, stale2.Articles.Count);
    }

    [Fact]
    public async Task SourceFilter_DoesNotMutateCachedResult()
    {
        var handler = new SwitchableHandler();
        RespondSuccess(handler);
        var svc = Service(handler);
        var fresh = await svc.RefreshAsync();
        var count = fresh.Articles.Count;
        Assert.NotEmpty(fresh.Articles);

        // Simulate what FloodNewsController.Index does with ?source=...
        var filtered = new FloodNewsResult
        {
            Articles = fresh.Articles.Where(a => a.Source == "NoSuchSource").ToList(),
            Trends = fresh.Trends,
            FetchedAtUtc = fresh.FetchedAtUtc
        };
        Assert.Empty(filtered.Articles);
        Assert.Equal(count, fresh.Articles.Count);
    }

    [Fact]
    public async Task Disabled_ReturnsUnavailable()
    {
        var svc = Service(new SwitchableHandler(), enabled: false);
        var r = await svc.GetLatestAsync();
        Assert.True(r.IsUnavailable);
        Assert.Empty(r.Articles);
    }

    [Fact]
    public async Task DuplicateStory_AcrossFeeds_IsDeduplicated()
    {
        var handler = new SwitchableHandler();
        RespondSuccess(handler);
        var svc = Service(handler, true, new List<string>
        {
            "https://example.com/ph-one.xml",
            "https://example.com/ph-two.xml"
        });
        var r = await svc.RefreshAsync();
        Assert.Single(r.Articles);
        Assert.Equal(1, r.Trends.Last7Days);
    }

    [Fact]
    public async Task IrrelevantBudgetStory_FromFeed_IsFiltered()
    {
        var handler = new SwitchableHandler
        {
            Responder = req =>
            {
                if (req.RequestUri!.AbsoluteUri.Contains("gdacs"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "<?xml version=\"1.0\"?><rss version=\"2.0\">" +
                            "<channel><title>GDACS</title></channel></rss>")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                        "<rss version=\"2.0\"><channel><title>Test</title><item>" +
                        "<title>Watchdogs question transparency in House OK of 2027 budget bill</title>" +
                        "<link>https://example.com/budget-bill</link>" +
                        "<description>Watchdog groups questioned the transparency of the House " +
                        "approval of the 2027 national budget, citing flood control allocations. " +
                        "Lawmakers defended the process in Manila.</description>" +
                        "<pubDate>" + DateTimeOffset.UtcNow.ToString("R") + "</pubDate>" +
                        "<category>Manila</category>" +
                        "</item></channel></rss>")
                };
            }
        };
        var svc = Service(handler);
        var r = await svc.RefreshAsync();
        Assert.Empty(r.Articles);
    }

    [Fact]
    public async Task RelatedLinksBox_DoesNotConferRelevance()
    {
        var handler = new SwitchableHandler
        {
            Responder = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<rss version=\"2.0\"><channel><title>Test</title><item>" +
                    "<title>Senate opens debate on tax reform package</title>" +
                    "<link>https://example.com/tax-reform</link>" +
                    "<description>Senators in Manila opened plenary debate on the tax reform " +
                    "package. READ: Flash floods hit Cebu City villages; rescue teams " +
                    "deployed as floodwaters rose.</description>" +
                    "<pubDate>" + DateTimeOffset.UtcNow.ToString("R") + "</pubDate>" +
                    "<category>Manila</category>" +
                    "</item></channel></rss>")
            }
        };
        var svc = Service(handler);
        var r = await svc.RefreshAsync();
        // The flood evidence belongs to a linked teaser, not the article.
        Assert.Empty(r.Articles);
    }
}

