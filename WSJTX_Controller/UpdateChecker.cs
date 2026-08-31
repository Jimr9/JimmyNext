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

    // Checks GitHub's "latest release" API for a Jimmy Next version newer than the one
    // currently running -- the same source of truth website/update.html (opened by the
    // F4/UpdateCheck hotkey) already uses, so the two ways of checking never disagree.
    // Deliberately a SEPARATE repo from production Jimmy's (jimr9/Jimmy): Jimmy Next must
    // never receive a production update, and production must never receive a Jimmy Next
    // one -- pointing both builds' UpdateChecker at the same repo's "latest release" would
    // hand whichever build asked first whatever the OTHER product's most recent release
    // happened to be, since the API has no way to filter "latest" by product.
    public static class UpdateChecker
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/jimr9/JimmyNext/releases/latest";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        static UpdateChecker()
        {
            // GitHub's API rejects requests with no User-Agent header.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("JimmyNext-WSJTX-Controller");
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

        // Independent audit finding 7, 2026-08-23 (HARDENING GAP, HIGH SECURITY IMPACT):
        // GitHub's own asset redirect chain -- confirmed real hosts an update download can
        // legitimately traverse. api.github.com's own "assets/{id}" endpoint 302s to
        // objects.githubusercontent.com (or, for some repos/CDN configurations, still
        // github.com itself) to serve the actual bytes; HttpClient follows redirects by
        // default, so the URL actually fetched from can differ from browser_download_url's own
        // host. Anything else is rejected before ANY download begins.
        private static readonly string[] AllowedUpdateHosts =
        {
            "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        };

        // Downloads to a fresh, randomly-named PRIVATE staging directory (not a predictable
        // filename directly in the shared, world-writable temp root) and returns the file's
        // path. Caller launches/cleans it up.
        //
        // Independent audit finding 7, 2026-08-23 (HARDENING GAP, HIGH SECURITY IMPACT,
        // confidence 98%): this used to write to Path.Combine(Path.GetTempPath(),
        // suggestedFileName) -- a predictable path any other process/user session on the
        // machine could race to pre-create (symlink, hardlink, or simply pre-populate with
        // different content before this download completes), and suggestedFileName came
        // straight from the GitHub API response's asset "name" field with no sanitization
        // against a path-traversal segment ("../"). Fixed here: a fresh GUID-named
        // subdirectory under temp (created with default, non-world-writable ACLs, matching
        // ordinary per-user temp-subfolder semantics) makes the destination unpredictable and
        // exclusively ours; Path.GetFileName strips any directory component from the
        // suggested name so it can never escape that subdirectory. Does not (yet) add
        // SHA-256/Authenticode verification -- that requires a published-checksum or
        // signing-policy decision on the release-publishing side, which this pass did not
        // make (see the final report); this closes the staging/host half of the finding, which
        // is fully code-resolvable without that decision.
        public static async Task<string> DownloadToTempAsync(string url, string suggestedFileName)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("No update download URL was provided.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"Refusing to download an update over a non-HTTPS URL: {url}");
            if (Array.IndexOf(AllowedUpdateHosts, parsed.Host) < 0)
                throw new InvalidOperationException($"Refusing to download an update from an unexpected host: {parsed.Host}");

            string safeFileName = Path.GetFileName(suggestedFileName ?? "");
            if (string.IsNullOrEmpty(safeFileName)) safeFileName = "JimmyUpdate.msi";

            string stagingDir = Path.Combine(Path.GetTempPath(), "JimmyUpdate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);
            string path = Path.Combine(stagingDir, safeFileName);

            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                // The actual response URI after following any redirect chain -- re-validated
                // here too, not just the original request URL, since a redirect could otherwise
                // hand the final download to a host never checked above.
                Uri finalUri = response.RequestMessage?.RequestUri ?? parsed;
                if (finalUri.Scheme != Uri.UriSchemeHttps || Array.IndexOf(AllowedUpdateHosts, finalUri.Host) < 0)
                    throw new InvalidOperationException($"Refusing to accept an update redirected to an unexpected host: {finalUri.Host}");

                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(fileStream).ConfigureAwait(false);
                }
            }
            return path;
        }
    }
}
