using System.Net;
using COMS_MVC.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Provider-level tests for the targeted Philippines flood/drainage search
/// feeds: publisher attribution, credibility filtering, headline cleanup, and
/// the strict Philippines + relevance gates. No live network is used.
/// </summary>
public sealed class FloodNewsProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _rss;

        public StaticHandler(string rss) => _rss = rss;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("gdacs"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>GDACS</title></channel></rss>")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_rss)
            });
        }
    }

    private FloodNewsService Service(string rss)
    {
        var options = Options.Create(new FloodNewsOptions
        {
            Enabled = true,
            GdacsFeedUrl = "https://example.com/gdacs.xml",
            PhilippineNewsFeeds = new List<string>(),
            TargetedSearchFeeds = new List<string> { "https://news.google.com/rss/search?q=flood+Philippines" },
            CacheMinutes = 20,
            MaxArticles = 30,
            MaxAgeDays = 7,
            HttpTimeoutSeconds = 10
        });
        var http = new HttpClient(new StaticHandler(rss)) { Timeout = TimeSpan.FromSeconds(10) };
        return new FloodNewsService(http, options, _cache, NullLogger<FloodNewsService>.Instance);
    }

    private static string GoogleStyleFeed(params string[] items) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>Search</title>"
        + string.Concat(items)
        + "</channel></rss>";

    private static string Item(
        string title, string publisher, string publisherUrl, string link, string pubDate,
        string description = "")
        => "<item><title>" + title + "</title>"
           + "<link>" + link + "</link>"
           + "<pubDate>" + pubDate + "</pubDate>"
           + "<description>" + description + "</description>"
           + "<source url=\"" + publisherUrl + "\">" + publisher + "</source>"
           + "</item>";

    private static string RfcDate() => DateTimeOffset.UtcNow.ToString("R");

    [Fact]
    public async Task GoogleSearchItem_UsesRealPublisher_AndStripsSuffix()
    {
        var rss = GoogleStyleFeed(Item(
            "Cebu floods displace families - Inquirer.net",
            "Inquirer.net", "https://www.inquirer.net", "https://example.com/cebu-flood", RfcDate()));
        var result = await Service(rss).RefreshAsync();

        var article = Assert.Single(result.Articles);
        Assert.Equal("Cebu floods displace families", article.Title);
        Assert.Equal("Inquirer.net", article.Source);
        Assert.Contains(article.AffectedAreas, a => a.Equals("Cebu", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AggregatorDescriptionThatRepeatsHeadline_IsDropped()
    {
        var rss = GoogleStyleFeed(Item(
            "Cebu floods displace families - Inquirer.net",
            "Inquirer.net", "https://www.inquirer.net", "https://example.com/cebu-flood", RfcDate(),
            "Cebu floods displace families Inquirer.net"));
        var result = await Service(rss).RefreshAsync();

        var article = Assert.Single(result.Articles);
        Assert.Equal(string.Empty, article.Snippet);
    }

    [Fact]
    public async Task SocialMediaPublisher_IsRejected()
    {
        var rss = GoogleStyleFeed(Item(
            "Flash floods hit Metro Manila - Facebook",
            "Facebook", "https://www.facebook.com", "https://example.com/fb-flood", RfcDate()));
        var result = await Service(rss).RefreshAsync();

        Assert.Empty(result.Articles);
    }

    [Fact]
    public async Task TrustedPhPublisher_AnchorsPlaceWithoutPhPlaceInTitle()
    {
        var rss = GoogleStyleFeed(Item(
            "Flood control project completed - Philippine News Agency",
            "Philippine News Agency", "https://www.pna.gov.ph", "https://example.com/pna-flood", RfcDate()));
        var result = await Service(rss).RefreshAsync();

        var article = Assert.Single(result.Articles);
        Assert.Equal("Philippine News Agency", article.Source);
        Assert.Contains(article.AffectedAreas, a => a.Equals("Philippines", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ForeignFlood_FromUnknownPublisher_IsRejected()
    {
        var rss = GoogleStyleFeed(Item(
            "Jakarta floods displace thousands - Reuters",
            "Reuters", "https://www.reuters.com", "https://example.com/jakarta-flood", RfcDate()));
        var result = await Service(rss).RefreshAsync();

        Assert.Empty(result.Articles);
    }

    [Fact]
    public async Task ForeignFlood_FromTrustedPhPublisher_IsRejected()
    {
        var rss = GoogleStyleFeed(Item(
            "After Nepal floods, loss and damage fund seeks way forward - Philippine News Agency",
            "Philippine News Agency", "https://www.pna.gov.ph", "https://example.com/nepal-flood", RfcDate()));
        var result = await Service(rss).RefreshAsync();

        Assert.Empty(result.Articles);
    }

    [Fact]
    public async Task NearDuplicateHeadlines_AreCollapsed()
    {
        var rss = GoogleStyleFeed(
            Item("Cebu flood control project completed in Mandaue - Inquirer.net",
                "Inquirer.net", "https://www.inquirer.net", "https://example.com/a", RfcDate()),
            Item("Cebu flood control projects completed in Mandaue - Philstar",
                "Philstar", "https://www.philstar.com", "https://example.com/b", RfcDate()));
        var result = await Service(rss).RefreshAsync();

        Assert.Single(result.Articles);
    }

    [Fact]
    public async Task UnrelatedNationalStory_IsRejected()
    {
        var rss = GoogleStyleFeed(Item(
            "Senate approves tax reform package - Inquirer.net",
            "Inquirer.net", "https://www.inquirer.net", "https://example.com/tax", RfcDate()));
        var result = await Service(rss).RefreshAsync();

        Assert.Empty(result.Articles);
    }

    public void Dispose() => _cache.Dispose();
}

