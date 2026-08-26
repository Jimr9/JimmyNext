using System;
using System.IO;

namespace WSJTX_Controller
{
    // Club Log issues one API key per registered application, not per user --
    // every Jimmy installation shares the same key, and end users never see or
    // enter it (there is no field for it in Options). The key must never be
    // stored in source code or committed to the repo, so it is resolved at
    // runtime, outside version control, using the first of these that's set:
    //
    //   1. Environment variable JIMMY_CLUBLOG_APIKEY -- the key value itself.
    //      Simplest option; no file paths involved at all. Used for local/dev
    //      builds and as the source for option 3 below.
    //   2. Environment variable JIMMY_CLUBLOG_KEYFILE -- full path to a local
    //      text file whose entire (trimmed) contents are the key. Useful if
    //      you'd rather keep the key in a file than an environment variable.
    //   3. clublog_key.txt next to Jimmy.exe -- this is the path that reaches
    //      real end-user installs. Jimmy.csproj's BeforeBuild/AfterBuild
    //      targets read the key from a private file outside the repo (path in
    //      Jimmy.csproj's ClubLogKeyFile property) and write it into
    //      bin\Release\ during a Release build; the WiX installer packages it
    //      alongside Jimmy.exe. The raw key never touches source control or an
    //      environment variable -- bin\ is gitignored and the file is
    //      generated fresh on each Release build.
    //   4. %LocalAppData%\Jimmy\clublog_key.txt -- a per-user fallback file
    //      location so a maintainer can drop the key in place with no
    //      environment configuration or rebuild at all. This path is computed
    //      at runtime (Environment.SpecialFolder.LocalApplicationData), so no
    //      machine-specific directory ever appears in source.
    //
    // If none of these resolve (e.g. a Release build made without the env var
    // set), Resolve() returns "" and Club Log-backed Rule Definition universes
    // simply stay unavailable -- nothing else in Jimmy depends on it.
    internal static class ClubLogAppKey
    {
        private const string EnvKeyVar     = "JIMMY_CLUBLOG_APIKEY";
        private const string EnvKeyFileVar = "JIMMY_CLUBLOG_KEYFILE";
        private const string FallbackFileName = "clublog_key.txt";

        public static string Resolve()
        {
            try
            {
                string direct = Environment.GetEnvironmentVariable(EnvKeyVar);
                if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

                string keyFile = Environment.GetEnvironmentVariable(EnvKeyFileVar);
                if (!string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile))
                    return File.ReadAllText(keyFile).Trim();

                string shipped = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FallbackFileName);
                if (File.Exists(shipped))
                    return File.ReadAllText(shipped).Trim();

                // Derived from the running assembly's own name (not a literal "Jimmy") so a
                // differently-named build (e.g. a side-by-side "Jimmy Next" release) looks in
                // its own data folder, consistent with LookupManager.DataRoot.
                string fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    System.Reflection.Assembly.GetExecutingAssembly().GetName().Name, FallbackFileName);
                if (File.Exists(fallback))
                    return File.ReadAllText(fallback).Trim();

                return "";
            }
            catch
            {
                return "";
            }
        }
    }
}
