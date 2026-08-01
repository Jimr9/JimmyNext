using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

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
                if (!(new JavaScriptSerializer().DeserializeObject(json) is Dictionary<string, object> dict))
                    return null;

                string tag = dict.TryGetValue("tag_name", out var t) ? t as string : null;
                int[] latest = ParseVersion(tag);
                int[] current = ParseVersion(currentVersion);
                if (latest == null || current == null) return null;
                if (CompareVersions(current, latest) >= 0) return null;

                Dictionary<string, object> msiAsset = null;
                if (dict.TryGetValue("assets", out var assetsObj) && assetsObj is IEnumerable assets)
                {
                    foreach (var item in assets)
                    {
                        if (item is Dictionary<string, object> asset &&
                            (asset.TryGetValue("name", out var nameObj) ? nameObj as string : null)
                                ?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            msiAsset = asset;
                            break;
                        }
                    }
                }
                if (msiAsset == null) return null;

                DateTime? published = null;
                if (dict.TryGetValue("published_at", out var pubRaw) &&
                    DateTime.TryParse(pubRaw as string, out var pub))
                {
                    published = pub;
                }

                return new UpdateInfo
                {
                    Version   = tag.TrimStart('v', 'V'),
                    Published = published,
                    MsiName   = msiAsset.TryGetValue("name", out var n) ? n as string : "JimmyUpdate.msi",
                    MsiUrl    = msiAsset.TryGetValue("browser_download_url", out var u) ? u as string : null,
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
