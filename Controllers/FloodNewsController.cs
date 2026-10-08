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
        public async Task<IActionResult> Index(bool refresh = false, CancellationToken ct = default)
        {
            var result = refresh
                ? await _floodNews.RefreshAsync(ct)
                : await _floodNews.GetLatestAsync(ct);
            if (result.RefreshThrottled)
            {
                TempData["AlertMessage"] = "News was refreshed moments ago — showing the latest available.";
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
