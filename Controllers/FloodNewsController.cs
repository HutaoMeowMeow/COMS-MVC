namespace COMS_MVC.Controllers
{
    /// <summary>
    /// Read-only flood-news feed for all authenticated users.
    /// Informational only: never touches flood-risk scoring.
    /// </summary>
    [Authorize]
    public class FloodNewsController : Controller
    {
        private readonly IFloodNewsService _floodNews;

        public FloodNewsController(IFloodNewsService floodNews)
        {
            _floodNews = floodNews;
        }

        [HttpGet]
        public async Task<IActionResult> Index(bool refresh = false, string? source = null, CancellationToken ct = default)
        {
            var result = refresh
                ? await _floodNews.RefreshAsync(ct)
                : await _floodNews.GetLatestAsync(ct);
            if (result.RefreshThrottled)
            {
                TempData["AlertMessage"] = "News was refreshed moments ago — showing the latest available.";
            }

            // Source filter (dropdown): applied to a copy so the shared
            // cached result is never mutated. Trends stay global.
            var sources = result.Articles
                .Select(a => a.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();
            ViewBag.Sources = sources;
            ViewBag.CurrentSource = source;
            if (!string.IsNullOrWhiteSpace(source)
                && sources.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                result = new FloodNewsResult
                {
                    Articles = result.Articles
                        .Where(a => a.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    Trends = result.Trends,
                    FetchedAtUtc = result.FetchedAtUtc,
                    IsStale = result.IsStale,
                    IsUnavailable = result.IsUnavailable,
                    RefreshThrottled = result.RefreshThrottled
                };
            }
            return View(result);
        }

        /// <summary>
        /// Async fragment for the Flood Risk Index panel. Called by the
        /// browser AFTER the core page renders, so news can never block it.
        /// Never throws: failures render the panel's unavailable state.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Panel(CancellationToken ct)
        {
            try
            {
                var result = await _floodNews.GetLatestAsync(ct);
                return PartialView("_FloodNewsPanel", result);
            }
            catch
            {
                return PartialView("_FloodNewsPanel", null);
            }
        }
    }
}
