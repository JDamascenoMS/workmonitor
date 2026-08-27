namespace FT_MONITOR.Helpers
{
    public static class Constants
    {
        public static string Organization = "devdiv";
        public static string Project = "devdiv";
        public static string Team = "devdiv";
        // PAT removed — user impersonation via MSAL.js SPA PKCE flow is used instead.
        // The Bearer token is passed from the browser in the X-DevOps-Token header.
        public static string ReportOutputPath = @"C:\_raiz\reports\output";
        public static string FormatExt = @".txt";
        public static string ApiUrlBase = $"https://dev.azure.com";
        public static string ApiVersion = "api-version=7.1";

        public static int LimitWorkItems = 200;

        public enum WorkItemType
        {
            Bug,
            Task,
            UserStory,
            Epic,
            Issue,
            Feature
        }

        public static Dictionary<string, string> ProjectAreaPath { get; set; } = new Dictionary<string, string>()
        {
            {"Project", "DevDiv\\NET Tools\\Project"},
            {"Webtools", "DevDiv\\NET Tools\\Web Tools"},
            {"Modernization Upgrade", "DevDiv\\.NET Modernization\\Upgrade"},
            {"Publish", "DevDiv\\.NET Modernization\\Azure Tools"},
            {"Migrate", "DevDiv\\.NET Modernization\\Migrate"},
        };
    }
}
