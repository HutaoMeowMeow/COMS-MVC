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
    /// flood + Philippines matching) plus a Philippine news RSS filtered by
    /// transparent flood keywords. Fetch-on-demand with IMemoryCache; the
    /// last good result is served stale on provider failure. All external
    /// calls stay server-side. Purely informational: never feeds risk scores.
    /// </summary>
    public sealed class FloodNewsService : IFloodNewsService
    {
        private const string CacheKey = "flood-news-v1";

        private static readonly string[] StrongKeywords =
        {
            "flash flood", "flash flooding", "flood risk", "flood warning", "flood advisory",
            "flood alert", "floodwater", "floodwaters", "storm surge",
            "flood", "floods", "flooding", "flooded", "inundation", "inundated",
            "overflow", "overflowing", "river overflow", "drainage overflow", "canal overflow",
            "overflowing river", "waterlogging",
            "baha", "pagbaha", "binaha", "binabaha", "lunop"
        };

        private static readonly string[] WeakKeywords =
        {
            "heavy rainfall", "heavy rain", "heavy rains", "torrential rain", "intense rainfall",
            "rainfall warning", "rainfall", "rains", "rain", "ulan",
            "typhoon", "bagyo", "tropical storm", "cyclone", "storm", "storms",
            "monsoon", "southwest monsoon", "habagat", "amihan", "itcz",
            "water level", "water levels", "drainage", "canal", "evacuation", "evacuate"
        };

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
                current.RefreshThrottled = true;
                return current;
            }
            var result = await FetchAndCacheAsync(forceRefresh: true, ct);
            _cache.Set(CacheKey + ":force-at", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
            return result;
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
                    .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
                    .Take(Math.Clamp(_options.MaxArticles, 1, 100))
                    .ToList();

                // Truthful emptiness: an empty merge is only genuinely empty
                // when EVERY source succeeded. If any source failed, keep the
                // last good result (marked stale) instead of wiping the page.
                if (articles.Count == 0 && anySourceFailed && lastGood != null && lastGood.Articles.Count > 0)
                {
                    lastGood.IsStale = true;
                    _cache.Set(CacheKey, lastGood, TimeSpan.FromMinutes(3));
                    return lastGood;
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
                    lastGood.IsStale = true;
                    return lastGood;
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
                    lastGood.IsStale = true;
                    return lastGood;
                }
                return new FloodNewsResult
                {
                    FetchedAtUtc = DateTimeOffset.UtcNow,
                    IsUnavailable = true
                };
            }
        }

        private static string NormalizeTitle(string title) =>
            Regex.Replace((title ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", "");

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
                var areas = DetectAreas(title + "\n" + snippet);
                articles.Add(new FloodNewsArticle
                {
                    Title = title,
                    Source = "GDACS",
                    PublishedAt = ParseDate((string?)item.Element("pubDate")),
                    Url = link,
                    Snippet = snippet,
                    AffectedAreas = areas.Count > 0 ? areas : new List<string> { "Philippines" },
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
                var placeText = fullText + "\n" + CategoryText(item);
                var snippet = Snippetize(string.IsNullOrWhiteSpace(bodyText)
                    ? (string?)item.Element(ContentNamespace + "encoded")
                    : bodyText);
                var haystack = title + "\n" + fullText;
                if (!IsFloodRelevant(haystack, out _))
                {
                    continue;
                }

                // PHILIPPINES-ONLY gate: gazetteer place OR an explicit
                // Philippines mention. Anything else is rejected — never
                // borrowed, never relabeled, never "Area not specified".
                var areas = DetectAreas(title + "\n" + placeText);
                if (areas.Count == 0)
                {
                    if (!MentionsPhilippines(haystack))
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
                    AffectedAreas = areas
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

        private static bool IsFloodRelevant(string text, out bool strong)
        {
            strong = false;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            var lower = text.ToLowerInvariant();
            foreach (var keyword in StrongKeywords)
            {
                if (HasWord(lower, keyword))
                {
                    strong = true;
                    return true;
                }
            }
            // Weak signals only count together: "rain" alone is never enough.
            var weakHits = WeakKeywords.Count(keyword => HasWord(lower, keyword));
            return weakHits >= 2;
        }

        private static bool HasWord(string lowerText, string keyword)
        {
            // Multi-word phrases: plain containment on normalized text.
            if (keyword.Contains(' '))
            {
                return lowerText.Contains(keyword, StringComparison.Ordinal);
            }
            return Regex.IsMatch(lowerText, @"\b" + Regex.Escape(keyword) + @"\b");
        }

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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<FloodNewsArticle>();
            foreach (var article in articles)
            {
                var key = NormalizeUrl(article.Url);
                if (string.IsNullOrEmpty(key))
                {
                    key = "title:" + Regex.Replace(
                        article.Title.ToLowerInvariant(), @"[^a-z0-9]+", "");
                }
                if (seen.Add(key))
                {
                    result.Add(article);
                }
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

        private static string NormalizeUrl(string url)
        {
            var cleaned = CleanUrl(url);
            if (string.IsNullOrEmpty(cleaned))
            {
                return string.Empty;
            }
            return cleaned.TrimEnd('/').ToLowerInvariant();
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
