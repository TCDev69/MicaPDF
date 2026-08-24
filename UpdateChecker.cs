using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace MicaPDF
{
    public sealed class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; init; }
        public string CurrentVersion { get; init; } = "";
        public string LatestVersion { get; init; } = "";
        public string ReleaseUrl { get; init; } = "";
        public string? Error { get; init; }
    }

    public static class UpdateChecker
    {
        private static readonly HttpClient Http = CreateClient();

        public static string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        public static async Task<UpdateCheckResult> CheckAsync(string repository)
        {
            var current = GetCurrentVersion();
            if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/'))
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    Error = Loc.Get("update.repoMissing")
                };
            }

            try
            {
                var url = $"https://api.github.com/repos/{repository.Trim()}/releases/latest";
                using var response = await Http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return new UpdateCheckResult
                    {
                        CurrentVersion = current,
                        Error = Loc.Format("update.githubStatus", (int)response.StatusCode)
                    };
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
                var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                var latest = NormalizeVersion(tag);
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = CompareVersions(latest, current) > 0,
                    CurrentVersion = current,
                    LatestVersion = latest,
                    ReleaseUrl = string.IsNullOrWhiteSpace(htmlUrl)
                        ? $"https://github.com/{repository.Trim()}/releases/latest"
                        : htmlUrl
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    Error = ex.Message
                };
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MicaPDF");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static string NormalizeVersion(string tag)
        {
            tag = tag.Trim();
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                tag = tag[1..];
            var parts = tag.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "0.0.0" : tag;
        }

        private static int CompareVersions(string a, string b)
        {
            var pa = Parse(a);
            var pb = Parse(b);
            for (var i = 0; i < 3; i++)
            {
                var cmp = pa[i].CompareTo(pb[i]);
                if (cmp != 0) return cmp;
            }

            return 0;
        }

        private static int[] Parse(string version)
        {
            var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var result = new[] { 0, 0, 0 };
            for (var i = 0; i < Math.Min(3, parts.Length); i++)
            {
                var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var n))
                    result[i] = n;
            }

            return result;
        }
    }
}
