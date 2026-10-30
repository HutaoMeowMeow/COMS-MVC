namespace COMS_MVC.Helpers
{
    /// <summary>
    /// Product-level maximum lengths for user-supplied text. These are
    /// enforced by model validation and echoed as maxlength attributes in
    /// forms; database column widths are kept wider for backward
    /// compatibility with existing rows.
    /// </summary>
    public static class InputLimits
    {
        public const int Username = 15;
        public const int FullName = 40;
        public const int CanalName = 25;
    }
}
