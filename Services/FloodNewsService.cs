using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Keyless configuration for the Flood News feature. Binds from
    /// appsettings.json -&gt; "FloodNews". No secrets are involved: both
    /// providers are public keyless RSS feeds. Follows the same
    /// Configure(GetSection) convention as Brevo/ImageVerification options.
    /// Railway override shape (only if a value ever needs changing):
    ///   FloodNews__Enabled, FloodNews__CacheMinutes, ...
    /// </summary>
    public class FloodNewsOptions
    {
        public bool Enabled { get; set; } = true;

        public string GdacsFeedUrl { get; set; } = "https://www.gdacs.org/xml/rss.xml";

        /// <summary>Preferred multi-feed list (all keyless PH news RSS).</summary>
        public List<string> PhilippineNewsFeeds { get; set; } = new()
        {
            "https://www.inquirer.net/fullfeed",
            "https://cebudailynews.inquirer.net/feed",
            "https://www.philstar.com/rss/nation",
            "https://www.sunstar.com.ph/feed",
            "https://www.rappler.com/feed/",
            "https://palawan-news.com/feed"
        };

        /// <summary>Back-compat single-feed value; appended when set.</summary>
        public string PhilippineNewsFeedUrl { get; set; } = string.Empty;

        /// <summary>Minutes a successful fetch stays in IMemoryCache.</summary>
        public int CacheMinutes { get; set; } = 20;

        public int MaxArticles { get; set; } = 30;

        /// <summary>Articles older than this are dropped.</summary>
        public int MaxAgeDays { get; set; } = 7;

        /// <summary>Per-fetch timeout so a slow provider never blocks a page.</summary>
        public int HttpTimeoutSeconds { get; set; } = 10;
    }

    /// <summary>
    /// Lightweight flood-news article. Headline + short snippet + link only;
    /// full article text is never stored or reproduced.
    /// </summary>
    public sealed class FloodNewsArticle
    {
        public string Title { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public DateTimeOffset? PublishedAt { get; set; }

        public string Url { get; set; } = string.Empty;

        public string Snippet { get; set; } = string.Empty;

        public List<string> AffectedAreas { get; set; } = new();

        /// <summary>
        /// "Advisory" (warnings/forecasts) vs "Report" (incidents) — derived
        /// from the article's own words, never invented severity.
        /// </summary>
        public string Kind { get; set; } = "Report";

        /// <summary>True for official government/disaster-data sources (GDACS).</summary>
        public bool IsOfficial { get; set; }
    }

    public sealed class FloodNewsTrends
    {
        public int Last24Hours { get; set; }

        public int Last7Days { get; set; }

        public List<string> TopAreas { get; set; } = new();
    }

    public sealed class FloodNewsResult
    {
        public List<FloodNewsArticle> Articles { get; set; } = new();

        public FloodNewsTrends Trends { get; set; } = new();

        public DateTimeOffset FetchedAtUtc { get; set; }

        /// <summary>True when the provider failed and these are older cached rows.</summary>
        public bool IsStale { get; set; }

        /// <summary>True when nothing could be shown (no cache, provider down).</summary>
        public bool IsUnavailable { get; set; }

        /// <summary>True when a manual refresh hit the anti-spam cooldown.</summary>
        public bool RefreshThrottled { get; set; }
    }

    public interface IFloodNewsService
    {
        Task<FloodNewsResult> GetLatestAsync(CancellationToken ct = default);

        /// <summary>
        /// Bypasses the read cache (2-minute manual-refresh cooldown applies).
        /// </summary>
        Task<FloodNewsResult> RefreshAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Keyless flood-news aggregation: GDACS disaster RSS (structured
    /// flood + Philippines matching) plus Philippine news RSS filtered by a
    /// strict flood-relevance classifier (see <see cref="FloodRelevance"/>).
    /// Fetch-on-demand with IMemoryCache; the last good result is served
    /// stale on provider failure. All external calls stay server-side.
    /// Purely informational: never feeds risk scores.
    /// </summary>
    public sealed class FloodNewsService : IFloodNewsService
    {
        // v4: stricter Philippines-focus rule for stories without a specific
        // PH place (foreign locations must not dominate), plus the v3 strict
        // flood-relevance classifier and political/budget-process veto.
        // Bumped so rows cached under older, looser rules are never served.
        private const string CacheKey = "flood-news-v4";

        // ---------- providers ----------

        /// <summary>
        /// Transparent Philippine gazetteer, longest names first so
        /// "Cebu City" wins over "Cebu". Word-boundary matched.
        /// </summary>
        private static readonly string[] Gazetteer =
        {
            "Metro Manila", "National Capital Region", "NCR", "Metro Cebu",
            "Central Luzon", "Calabarzon", "Mimaropa", "Bicol Region",
            "Western Visayas", "Central Visayas", "Eastern Visayas",
            "Northern Mindanao", "Davao Region", "Soccsksargen", "Caraga",
            "Bangsamoro", "Cordillera", "Cagayan Valley", "Ilocos Region",
            "Cebu City", "Lapu-Lapu City", "Mandaue City", "Talisay City",
            "Danao City", "Toledo City", "Naga City", "Carcar City", "Bogo City",
            "Consolacion", "Liloan", "Compostela", "Cordova", "Minglanilla",
            "San Fernando City", "Sibonga", "Argao", "Dalaguete", "Bantayan",
            "Madridejos", "Santa Fe", "Daanbantayan", "Medellin", "San Remigio",
            "Tabuelan", "Tuburan", "Asturias", "Balamban", "Pinamungajan",
            "Aloguinsan", "Sogod", "Catmon", "Carmen", "Mandaue", "Lapu-Lapu",
            "Tagbilaran City", "Talibon", "Ubay", "Bohol",
            "Dumaguete City", "San Carlos City", "Kabankalan City", "Victorias City",
            "Silay City", "Bais City", "Negros",
            "Ormoc City", "Baybay City", "Maasin City", "Tacloban City", "Leyte",
            "Calbayog City", "Catbalogan City", "Borongan City", "Samar",
            "Naval", "Biliran", "Southern Leyte", "Eastern Samar", "Northern Samar",
            "Roxas City", "Kalibo", "San Jose de Buenavista", "Passi City",
            "Iloilo City", "Bacolod City", "Panay", "Guimaras", "Aklan", "Antique",
            "Capiz", "Iloilo",
            "Puerto Princesa City", "Puerto Princesa", "Coron", "El Nido",
            "Brookes Point", "Brooke's Point", "Narra", "Quezon, Palawan", "Palawan",
            "Quezon City", "Caloocan City", "Marikina City", "Marikina", "Pasig City",
            "Pasig", "Valenzuela City", "Malabon City", "Malabon", "Navotas City",
            "Navotas", "Paranaque City", "Paranaque", "Las Pinas City", "Las Pinas",
            "Muntinlupa City", "Muntinlupa", "Makati City", "Makati",
            "Mandaluyong City", "Mandaluyong", "San Juan City", "San Juan",
            "Pasay City", "Pasay", "Manila", "Taguig City", "Taguig", "Pateros",
            "Malolos City", "Malolos", "Meycauayan City", "Meycauayan", "Marilao",
            "Bocaue", "Santa Maria", "Bulacan", "Plaridel", "Calumpit", "Hagonoy",
            "San Fernando", "Angeles City", "Mabalacat City", "Mabalacat", "Guagua",
            "Arayat", "Candaba", "San Luis, Pampanga", "Santa Ana, Pampanga",
            "Apalit", "Macabebe", "Masantol", "Pampanga", "Tarlac City", "Tarlac",
            "Olongapo City", "Subic", "Pangasinan", "Dagupan City", "Alaminos City",
            "Urdaneta City", "Laoag City", "Vigan City", "Candon City", "Ilocos",
            "Tuguegarao City", "Ilagan City", "Ilagan", "Cauayan City", "Cauayan",
            "Santiago City", "Aparri", "Cagayan", "Isabela", "Batanes", "Nueva Vizcaya",
            "Quirino", "Aurora", "Nueva Ecija", "Cabanatuan City", "Palayan City",
            "Calamba City", "Calamba", "San Pablo City", "San Pablo", "Binan City",
            "Binan", "Santa Rosa City", "Santa Rosa", "Santa Cruz", "Laguna", "Cavite",
            "Bacoor City", "Bacoor", "Imus City", "Imus", "Dasmariñas City", "Dasmarinas",
            "Bauan", "Lemery", "Taal", "Tanauan City", "Tanauan", "Santo Tomas",
            "Batangas City", "Batangas", "Rizal", "Antipolo City", "Antipolo",
            "Taytay", "Cainta", "Rodriguez", "Montalban", "San Mateo",
            "Iriga City", "Iriga", "Tabaco City", "Tabaco", "Sorsogon City",
            "Masbate City", "Daet", "Naga City", "Legazpi City",
            "Camarines Sur", "Camarines Norte", "Albay", "Sorsogon", "Catanduanes",
            "Masbate", "Romblon", "Marinduque", "Occidental Mindoro", "Oriental Mindoro",
            "Calapan City", "Calapan", "Naujan", "Bukidnon",
            "Valencia City", "Malaybalay City", "Misamis Oriental", "Misamis Occidental",
            "Ozamiz City", "Ozamiz", "Oroquieta City", "Tangub City",
            "Cagayan de Oro", "Iligan City", "El Salvador City", "Gingoog City",
            "Lanao del Norte", "Lanao del Sur", "Marawi City", "Marawi", "Tubod",
            "Pagadian City", "Pagadian", "Dipolog City", "Dapitan City",
            "Davao City", "Tagum City", "Tagum", "Panabo City", "Panabo",
            "Digos City", "Digos", "Mati City", "Mati", "Davao del Norte",
            "Davao del Sur", "Davao Oriental", "Davao de Oro",
            "Kidapawan City", "Kidapawan", "Tacurong City", "Tacurong",
            "Koronadal City", "Koronadal", "Isulan", "Cotabato", "Cotabato City",
            "Midsayap", "Kabacan", "Maguindanao", "Sultan Kudarat", "South Cotabato",
            "Sarangani", "General Santos", "Zamboanga City", "Zamboanga del Norte",
            "Zamboanga del Sur", "Zamboanga Sibugay", "Isabela City", "Lamitan City",
            "Basilan", "Sulu", "Jolo", "Tawi-Tawi", "Agusan del Norte", "Agusan del Sur",
            "Butuan City", "Cabadbaran City", "Bayugan City", "Tandag City", "Bislig City",
            "Surigao del Norte", "Surigao del Sur", "Surigao City", "Dinagat Islands",
            "Baguio City", "Tabuk City", "Tabuk", "Ifugao", "Benguet", "Mountain Province",
            "Kalinga", "Apayao", "Abra", "Bontoc", "La Trinidad",
            "Luzon", "Visayas", "Mindanao", "Philippines", "Philippine"
        };

        private readonly HttpClient _http;
        private readonly FloodNewsOptions _options;
        private readonly IMemoryCache _cache;
        private readonly ILogger<FloodNewsService> _logger;

        public FloodNewsService(
            HttpClient http,
            IOptions<FloodNewsOptions> options,
            IMemoryCache cache,
            ILogger<FloodNewsService> logger)
        {
            _http = http;
            _options = options.Value;
            _cache = cache;
            _logger = logger;
        }

        public async Task<FloodNewsResult> GetLatestAsync(CancellationToken ct = default)
        {
            if (!_options.Enabled)
            {
                return new FloodNewsResult
                {
                    FetchedAtUtc = DateTimeOffset.UtcNow,
                    IsUnavailable = true
                };
            }

            if (_cache.TryGetValue<FloodNewsResult>(CacheKey, out var cached) && cached != null)
            {
                return cached;
            }

            return await FetchAndCacheAsync(forceRefresh: false, ct);
        }

        public async Task<FloodNewsResult> RefreshAsync(CancellationToken ct = default)
        {
            // Anti-spam cooldown: at most one forced provider round-trip
            // every 2 minutes; otherwise serve the current cache.
            if (_cache.TryGetValue<DateTimeOffset>(CacheKey + ":force-at", out var lastForce)
                && DateTimeOffset.UtcNow - lastForce < TimeSpan.FromMinutes(2)
                && _cache.TryGetValue<FloodNewsResult>(CacheKey, out var current) && current != null)
            {
                // Copy (including a fresh Articles list) so callers can filter
                // or reshape the response without corrupting the shared cache.
                return CopyResult(current, refreshThrottled: true);
            }
            var result = await FetchAndCacheAsync(forceRefresh: true, ct);
            _cache.Set(CacheKey + ":force-at", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
            return result;
        }

        /// <summary>
        /// Defensive copy of a cached result. The Articles list (and trend
        /// areas) are cloned so a caller that filters, sorts, or clears the
        /// returned response can never corrupt the shared cached object.
        /// </summary>
        private static FloodNewsResult CopyResult(
            FloodNewsResult source, bool isStale = false, bool refreshThrottled = false)
        {
            return new FloodNewsResult
            {
                Articles = source.Articles.ToList(),
                Trends = new FloodNewsTrends
                {
                    Last24Hours = source.Trends.Last24Hours,
                    Last7Days = source.Trends.Last7Days,
                    TopAreas = source.Trends.TopAreas.ToList()
                },
                FetchedAtUtc = source.FetchedAtUtc,
                IsStale = isStale || source.IsStale,
                IsUnavailable = source.IsUnavailable,
                RefreshThrottled = refreshThrottled || source.RefreshThrottled
            };
        }

        private async Task<FloodNewsResult> FetchAndCacheAsync(bool forceRefresh, CancellationToken ct)
        {
            FloodNewsResult? lastGood = null;
            if (_cache.TryGetValue<FloodNewsResult>(CacheKey + ":last-good", out var lg) && lg != null)
            {
                lastGood = lg;
            }

            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(Math.Clamp(_options.HttpTimeoutSeconds, 5, 60)));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

                // All feeds in parallel: one slow provider never multiplies the wait.
                // Each feed is isolated: one failure never affects the others.
                var gdacsTask = FetchGdacsAsync(linked.Token);
                var newsTasks = FeedUrls()
                    .Select(feed => FetchPhilippineNewsAsync(feed.Url, feed.Source, linked.Token))
                    .ToList();
                var allTasks = new List<Task> { gdacsTask };
                allTasks.AddRange(newsTasks);
                await Task.WhenAll(allTasks);

                // PHILIPPINES-ONLY: every pool below already rejects non-PH
                // items at the source. No international fallback exists.
                var combined = new List<FloodNewsArticle>();
                var anySourceFailed = false;
                var (gdacsArticles, gdacsOk) = await gdacsTask;
                anySourceFailed |= !gdacsOk;
                combined.AddRange(gdacsArticles);
                foreach (var task in newsTasks)
                {
                    var (feedArticles, feedOk) = await task;
                    anySourceFailed |= !feedOk;
                    combined.AddRange(feedArticles);
                }

                var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.MaxAgeDays));
                var articles = Deduplicate(combined)
                    .Where(a => !a.PublishedAt.HasValue || a.PublishedAt.Value >= cutoff)
                    .OrderByDescending(a => FloodRelevance.IsCebuPriority(a.AffectedAreas))
                    .ThenByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
                    .Take(Math.Clamp(_options.MaxArticles, 1, 100))
                    .ToList();

                // Truthful emptiness: an empty merge is only genuinely empty
                // when EVERY source succeeded. If any source failed, keep the
                // last good result (marked stale) instead of wiping the page.
                if (articles.Count == 0 && anySourceFailed && lastGood != null && lastGood.Articles.Count > 0)
                {
                    var stale = CopyResult(lastGood, isStale: true);
                    _cache.Set(CacheKey, stale, TimeSpan.FromMinutes(3));
                    return stale;
                }

                var result = new FloodNewsResult
                {
                    Articles = articles,
                    Trends = BuildTrends(articles),
                    FetchedAtUtc = DateTimeOffset.UtcNow
                };

                var minutes = Math.Clamp(_options.CacheMinutes, 5, 120);
                _cache.Set(CacheKey, result, TimeSpan.FromMinutes(minutes));
                _cache.Set(CacheKey + ":last-good", result, TimeSpan.FromHours(24));
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Flood news provider failed; falling back to last good cache if present.");
                if (lastGood != null)
                {
                    return CopyResult(lastGood, isStale: true);
                }
                return new FloodNewsResult
                {
                    FetchedAtUtc = DateTimeOffset.UtcNow,
                    IsUnavailable = true
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Flood news fetch timed out; falling back to last good cache if present.");
                if (lastGood != null)
                {
                    // Never mutate the shared cached object: return a copy
                    // flagged stale so concurrent readers see consistent data.
                    return CopyResult(lastGood, isStale: true);
                }
                return new FloodNewsResult
                {
                    FetchedAtUtc = DateTimeOffset.UtcNow,
                    IsUnavailable = true
                };
            }
        }

        private List<(string Url, string Source)> FeedUrls()
        {
            var urls = new List<(string Url, string Source)>();
            if (_options.PhilippineNewsFeeds != null)
            {
                foreach (var u in _options.PhilippineNewsFeeds.Where(u => !string.IsNullOrWhiteSpace(u)))
                {
                    urls.Add((u, SourceNameFor(u)));
                }
            }
            // Back-compat single value.
            if (!string.IsNullOrWhiteSpace(_options.PhilippineNewsFeedUrl)
                && !urls.Any(x => x.Url == _options.PhilippineNewsFeedUrl))
            {
                urls.Add((_options.PhilippineNewsFeedUrl, SourceNameFor(_options.PhilippineNewsFeedUrl)));
            }
            return urls.GroupBy(x => x.Url).Select(g => g.First()).ToList();
        }

        private static string SourceNameFor(string url)
        {
            if (url.Contains("cebudailynews", StringComparison.OrdinalIgnoreCase))
            {
                return "Cebu Daily News";
            }
            if (url.Contains("philstar", StringComparison.OrdinalIgnoreCase))
            {
                return "Philstar";
            }
            if (url.Contains("sunstar", StringComparison.OrdinalIgnoreCase))
            {
                return "SunStar";
            }
            if (url.Contains("rappler", StringComparison.OrdinalIgnoreCase))
            {
                return "Rappler";
            }
            if (url.Contains("palawan-news", StringComparison.OrdinalIgnoreCase))
            {
                return "Palawan News";
            }
            if (url.Contains("inquirer", StringComparison.OrdinalIgnoreCase))
            {
                return "Inquirer";
            }
            return "PH News";
        }

        // ---------- providers ----------

        private async Task<(List<FloodNewsArticle> Articles, bool Ok)> FetchGdacsAsync(CancellationToken ct)
        {
            var articles = new List<FloodNewsArticle>();
            string xml;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _options.GdacsFeedUrl);
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("GDACS feed returned {Status}.", (int)response.StatusCode);
                    return (articles, false);
                }
                xml = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GDACS feed fetch failed.");
                return (articles, false);
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GDACS feed XML could not be parsed.");
                return (articles, false);
            }

            XNamespace gdacs = "http://www.gdacs.org";
            foreach (var item in doc.Descendants("item"))
            {
                var eventType = (string?)item.Element(gdacs + "eventtype") ?? string.Empty;
                var country = (string?)item.Element(gdacs + "country") ?? string.Empty;
                var iso3 = (string?)item.Element(gdacs + "iso3") ?? string.Empty;

                // PRIMARY geographic filter: structured GDACS metadata.
                // ONLY Philippine events are accepted — everything else is
                // rejected outright. No keyword fallback, no placeless fallback.
                var isPhilippines = iso3.Equals("PHL", StringComparison.OrdinalIgnoreCase)
                    || country.Equals("Philippines", StringComparison.OrdinalIgnoreCase);
                if (!isPhilippines)
                {
                    continue;
                }

                var isFlood = eventType.Equals("FL", StringComparison.OrdinalIgnoreCase);
                var isPhilippineStorm = eventType.Equals("TC", StringComparison.OrdinalIgnoreCase);
                if (!isFlood && !isPhilippineStorm)
                {
                    continue;
                }

                var title = CleanText((string?)item.Element("title"));
                var link = CleanUrl((string?)item.Element("link"));
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link))
                {
                    continue;
                }

                var snippet = Snippetize((string?)item.Element("description"));

                // FL (structured official flood classification) is authoritative.
                // Tropical-cyclone items must additionally show real flood
                // substance in their own text — a storm without flooding is out.
                if (!isFlood && !FloodRelevance.IsRelevant(title, snippet))
                {
                    continue;
                }

                var areas = DetectAreas(title + "\n" + snippet);
                articles.Add(new FloodNewsArticle
                {
                    Title = title,
                    Source = "GDACS",
                    PublishedAt = ParseDate((string?)item.Element("pubDate")),
                    Url = link,
                    Snippet = snippet,
                    AffectedAreas = areas.Count > 0 ? areas : new List<string> { "Philippines" },
                    Kind = FloodRelevance.TopicLabel(title, snippet),
                    IsOfficial = true
                });
            }
            return (articles, true);
        }

        private async Task<(List<FloodNewsArticle> Articles, bool Ok)> FetchPhilippineNewsAsync(
            string feedUrl, string sourceName, CancellationToken ct)
        {
            var articles = new List<FloodNewsArticle>();

            string xml;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("PH news feed {Feed} returned {Status}.", sourceName, (int)response.StatusCode);
                    return (articles, false);
                }
                xml = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PH news feed {Feed} fetch failed.", sourceName);
                return (articles, false);
            }

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PH news feed {Feed} XML could not be parsed.", sourceName);
                return (articles, false);
            }

            foreach (var item in doc.Descendants("item"))
            {
                var title = CleanText((string?)item.Element("title"));
                var link = CleanUrl((string?)item.Element("link"));
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link))
                {
                    continue;
                }

                // Some publishers (e.g. SunStar) leave <description/> empty and
                // put the body in content:encoded; categories often carry the
                // location. Use all three for matching; the visible snippet
                // stays a short excerpt (never full text).
                // Categories carry places ("Cebu", "Pampanga") but also
                // topical tags, so they feed location detection ONLY —
                // never keyword matching.
                var bodyText = CleanText((string?)item.Element("description"));
                var fullText = bodyText + "\n" + ContentEncodedText(item);
                // Publisher "related stories / read more" boxes appended to the
                // body can inject another story's flood keywords into THIS
                // article. Strip those tails before matching (same markers the
                // visible snippet uses), so relevance is judged on the
                // article's own content only.
                var matchText = StripLinkBoilerplate(fullText);
                var placeText = matchText + "\n" + CategoryText(item);
                var snippet = Snippetize(string.IsNullOrWhiteSpace(bodyText)
                    ? (string?)item.Element(ContentNamespace + "encoded")
                    : bodyText);
                var haystack = title + "\n" + matchText;
                // Strict relevance gate: flooding must be the primary subject
                // or a substantial part — rain/storm/government mentions alone
                // never qualify. Applied before save/cache/count/display.
                if (!FloodRelevance.IsRelevant(title, matchText))
                {
                    continue;
                }

                // PHILIPPINES-ONLY gate (both filters must pass together):
                // a specific PH place anchors the story; otherwise an explicit
                // country mention is required AND foreign locations must not
                // dominate (foreign floods / incidental roundup mentions out).
                // Never inferred from the publisher; tags only from article text.
                var areas = DetectAreas(title + "\n" + placeText);
                var specificAnchor = areas.Any(a =>
                    !a.Equals("Philippines", StringComparison.OrdinalIgnoreCase) &&
                    !a.Equals("Philippine", StringComparison.OrdinalIgnoreCase));
                if (!specificAnchor)
                {
                    if (!MentionsPhilippines(haystack)
                        || !FloodRelevance.HasPhilippineFocus(title + "\n" + placeText))
                    {
                        continue;
                    }
                    areas = new List<string> { "Philippines" };
                }

                articles.Add(new FloodNewsArticle
                {
                    Title = title,
                    Source = sourceName,
                    PublishedAt = ParseDate((string?)item.Element("pubDate")),
                    Url = link,
                    Snippet = snippet,
                    AffectedAreas = areas,
                    Kind = FloodRelevance.TopicLabel(title, snippet)
                });
            }
            return (articles, true);
        }

        private static readonly XNamespace ContentNamespace = "http://purl.org/rss/1.0/modules/content/";

        private static string ContentEncodedText(XElement item) =>
            CleanText((string?)item.Element(ContentNamespace + "encoded"));

        private static string CategoryText(XElement item) =>
            string.Join(" ", item.Elements("category").Select(c => CleanText(c.Value)));

        /// <summary>
        /// Explicit Philippines mention with strict word boundaries, so "PH"
        /// inside other words never matches.
        /// </summary>
        private static bool MentionsPhilippines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            return Regex.IsMatch(text,
                @"\b(philippines|philippine|filipino|filipina|pilipinas|phl|ph)\b",
                RegexOptions.IgnoreCase);
        }

        // ---------- filtering / matching ----------

        private static List<string> DetectAreas(string text)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return found;
            }
            // Gazetteer is longest-first, so "Cebu City" matches before "Cebu".
            foreach (var place in Gazetteer)
            {
                if (found.Count >= 3)
                {
                    break;
                }
                var pattern = place.Contains(' ')
                    ? Regex.Escape(place)
                    : @"\b" + Regex.Escape(place) + @"\b";
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)
                    && !found.Any(f => f.Contains(place, StringComparison.OrdinalIgnoreCase)
                        || place.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(place);
                }
            }
            return found;
        }

        private static List<FloodNewsArticle> Deduplicate(List<FloodNewsArticle> articles)
        {
            // Same URL (case/trailing-slash insensitive) OR same normalized
            // title counts as a duplicate — catches the same story carried by
            // different providers so counts are never inflated.
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<FloodNewsArticle>();
            foreach (var article in articles)
            {
                var urlKey = (article.Url ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
                var titleKey = "title:" + Regex.Replace(
                    article.Title.ToLowerInvariant(), @"[^a-z0-9]+", "");
                if (!string.IsNullOrEmpty(urlKey))
                {
                    if (!seenUrls.Add(urlKey) || seenTitles.Contains(titleKey))
                        continue;
                }
                else if (!seenTitles.Add(titleKey))
                {
                    continue;
                }
                seenTitles.Add(titleKey);
                result.Add(article);
            }
            return result;
        }

        private static FloodNewsTrends BuildTrends(List<FloodNewsArticle> articles)
        {
            var now = DateTimeOffset.UtcNow;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var article in articles)
            {
                foreach (var area in article.AffectedAreas)
                {
                    counts.TryGetValue(area, out var n);
                    counts[area] = n + 1;
                }
            }
            return new FloodNewsTrends
            {
                // Informational news frequency only — never a severity score.
                Last24Hours = articles.Count(a => a.PublishedAt.HasValue && a.PublishedAt.Value >= now.AddHours(-24)),
                Last7Days = articles.Count,
                TopAreas = counts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key).ToList()
            };
        }

        // ---------- text helpers ----------

        private static string CleanText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            var decoded = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " "));
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        private static string Snippetize(string? html, int maxLength = 220)
        {
            var text = CleanText(html);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            // Strip common publisher boilerplate tails.
            foreach (var marker in new[] { "Keep on reading", "READ:" })
            {
                var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index > 40)
                {
                    text = text[..index].Trim();
                }
            }
            if (text.Length <= maxLength)
            {
                return text;
            }
            var cut = text[..maxLength];
            var lastSpace = cut.LastIndexOf(' ');
            if (lastSpace > maxLength / 2)
            {
                cut = cut[..lastSpace];
            }
            return cut.TrimEnd() + "…";
        }

        /// <summary>
        /// Removes publisher "related stories / read more" tails from the
        /// text used for relevance matching. Markers are the same tail
        /// markers the visible snippet uses, with the same position guard:
        /// a marker in the first 40 characters is article content, not a
        /// trailing link box, and is left alone.
        /// </summary>
        private static string StripLinkBoilerplate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var cut = text.Length;
            foreach (var marker in new[] { "Keep on reading", "READ:", "READ NEXT" })
            {
                var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index > 40 && index < cut)
                {
                    cut = index;
                }
            }
            return text[..cut].Trim();
        }

        private static string CleanUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            var candidate = value.Trim();
            // Server-side allow-list: browser only ever receives http/https.
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.ToString();
            }
            return string.Empty;
        }

        private static DateTimeOffset? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return DateTimeOffset.TryParse(value.Trim(), out var parsed) ? parsed : null;
        }
    }
}
