using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    public class UpdateInfo
    {
        public string   Version;
        public DateTime? Published;
        public string   MsiName;
        public string   MsiUrl;
    }

    // Checks GitHub's "latest release" API for a Jimmy version newer than the one
    // currently running -- the same source of truth website/update.html (opened by the
    // F4/UpdateCheck hotkey) already uses, so the two ways of checking never disagree.
    public static class UpdateChecker
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/jimr9/Jimmy/releases/latest";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        static UpdateChecker()
        {
            // GitHub's API rejects requests with no User-Agent header.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jimmy-WSJTX-Controller");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        // Returns null both when Jimmy is already up to date and when the check itself
        // failed (network down, GitHub rate limit, unparsable response) -- a startup
        // check must never nag or interrupt the user just because it couldn't complete.
        public static async Task<UpdateInfo> CheckForNewerVersionAsync(string currentVersion)
        {
            try
            {
                string json = await _http.GetStringAsync(ReleasesApiUrl).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                string tag = root.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String
                    ? tagEl.GetString() : null;
                int[] latest = ParseVersion(tag);
                int[] current = ParseVersion(currentVersion);
                if (latest == null || current == null) return null;
                if (CompareVersions(current, latest) >= 0) return null;

                JsonElement? msiAsset = null;
                if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String &&
                            nameEl.GetString()?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            msiAsset = asset;
                            break;
                        }
                    }
                }
                if (msiAsset == null) return null;
                JsonElement msi = msiAsset.Value;

                DateTime? published = null;
                if (root.TryGetProperty("published_at", out var pubEl) && pubEl.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(pubEl.GetString(), out var pub))
                {
                    published = pub;
                }

                return new UpdateInfo
                {
                    Version   = tag.TrimStart('v', 'V'),
                    Published = published,
                    MsiName   = msi.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "JimmyUpdate.msi",
                    MsiUrl    = msi.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                };
            }
            catch
            {
                return null;
            }
        }

        private static int[] ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            string[] parts = v.Trim().TrimStart('v', 'V').Split('.');
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out nums[i])) return null;
            }
            return nums;
        }

        private static int CompareVersions(int[] a, int[] b)
        {
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x - y;
            }
            return 0;
        }

        // Downloads to a fresh file in the user's temp folder and returns its path.
        // Caller launches/cleans it up.
        public static async Task<string> DownloadToTempAsync(string url, string suggestedFileName)
        {
            string path = Path.Combine(Path.GetTempPath(),
                string.IsNullOrEmpty(suggestedFileName) ? "JimmyUpdate.msi" : suggestedFileName);

            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(fileStream).ConfigureAwait(false);
                }
            }
            return path;
        }
    }
}
