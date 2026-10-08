using System;
using System.IO;

namespace WSJTX_Controller
{
    // What the support feature keeps on this computer (2026-10-07), in its own Support folder
    // beside the settings: whether Options shows Support and Support Helper, each recipient's
    // download code and the helper's login (both Windows-protected), the name and email last
    // used for a support request, and two local copies of support settings -- "my defaults"
    // (saved by the operator) and the last support settings installed. None of it is in a
    // profile, so none of it travels in a profile, a support package, a customization file or
    // Export Everything (which copies only the main settings files and profiles); the support
    // report skips the folder too (SupportReportBuilder).
    internal static class SupportLocal
    {
        internal const string FolderName = "Support";
        internal static string TestFolder;   // tests only

        internal static string Folder => TestFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Name, FolderName);

        internal static string MyDefaultsPath => Path.Combine(Folder, "my-defaults.zip");
        internal static string LastInstalledPath => Path.Combine(Folder, "last-installed.zip");

        private static IniFile Ini()
        {
            Directory.CreateDirectory(Folder);
            return new IniFile(Path.Combine(Folder, "support.ini"));
        }

        private static string Read(string key) { try { return Ini().Read(key) ?? ""; } catch { return ""; } }
        private static void Write(string key, string value) => Ini().Write(key, value);

        internal static bool ShowSupport
        {
            get => Read("showSupport") == "True";
            set => Write("showSupport", value ? "True" : null);
        }

        internal static bool ShowHelper
        {
            get => Read("showSupportHelper") == "True";
            set => Write("showSupportHelper", value ? "True" : null);
        }

        // The private switches (Controller start): --support shows Support, --support-helper both.
        internal static void ApplySwitches(string[] args)
        {
            bool helper = Array.Exists(args ?? new string[0], a => string.Equals(a, "--support-helper", StringComparison.OrdinalIgnoreCase));
            bool support = helper || Array.Exists(args ?? new string[0], a => string.Equals(a, "--support", StringComparison.OrdinalIgnoreCase));
            try
            {
                if (support && !ShowSupport) ShowSupport = true;
                if (helper && !ShowHelper) ShowHelper = true;
            }
            catch { }
        }

        // A recipient's download code, per base callsign. Key names say "token" so anything that
        // blanks secrets (SupportReportBuilder.IsSecretSetting) blanks these too.
        internal static string Code(string callsign)
        {
            string call = SupportService.BaseCallsign(callsign);
            if (call == null) return null;
            string stored = Read("downloadToken_" + call);
            return stored.Length == 0 ? null : SupportService.NormalizeCode(CredentialProtector.Unprotect(stored));
        }

        internal static void SetCode(string callsign, string code)
        {
            string call = SupportService.BaseCallsign(callsign);
            if (call == null) return;
            string c = SupportService.NormalizeCode(code);
            Write("downloadToken_" + call, c == null ? null : CredentialProtector.Protect(c));
        }

        internal static string HelperUser
        {
            get => Read("helperUser");
            set => Write("helperUser", string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant());
        }

        internal static string HelperPassword
        {
            get { string s = Read("helperPassword"); return s.Length == 0 ? "" : CredentialProtector.Unprotect(s); }
            set => Write("helperPassword", string.IsNullOrEmpty(value) ? null : CredentialProtector.Protect(value));
        }

        internal static string SenderName { get => Read("senderName"); set => Write("senderName", value); }
        internal static string SenderEmail { get => Read("senderEmail"); set => Write("senderEmail", value); }
    }
}
