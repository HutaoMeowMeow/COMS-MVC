namespace COMS_MVC.Models
{
    /// <summary>Shared server-rendered empty state (no client-side rendering).</summary>
    public sealed class EmptyStateViewModel
    {
        public string Icon { get; set; } = "bi-inbox";

        public string Title { get; set; } = "Nothing here yet";

        public string Message { get; set; } = string.Empty;

        public string? ActionText { get; set; }

        public string? ActionController { get; set; }

        public string? ActionName { get; set; }

        public object? ActionRouteValues { get; set; }
    }

    /// <summary>Shared server-rendered pagination bar.</summary>
    public sealed class PaginationViewModel
    {
        public int Page { get; set; } = 1;

        public int TotalPages { get; set; } = 1;

        public int TotalCount { get; set; }

        public string Action { get; set; } = "Index";

        public string? Controller { get; set; }

        public string Label { get; set; } = "records";

        /// <summary>Extra query values to keep while paging (filters).</summary>
        public Dictionary<string, string> RouteValues { get; set; } = new();
    }
}
