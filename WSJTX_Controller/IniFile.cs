using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

// Change this to match your program's normal namespace
namespace WSJTX_Controller
{
    public class IniFile   // revision 11
    {
        string Path;
        string EXE = Assembly.GetExecutingAssembly().GetName().Name;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern long WritePrivateProfileString(string Section, string Key, string Value, string FilePath);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);

        public IniFile(string IniPath = null)
        {
            Path = new FileInfo(IniPath ?? EXE + ".ini").FullName;
        }

        // Profiles feature, 2026-08-24: "Save Current Configuration As Profile" flushes every
        // setting to this instance's own file (the normal save path, unchanged) and then needs
        // to copy that exact file to a new named-profile path -- this is the only way it learns
        // where "this exact file" actually lives.
        public string FilePath => Path;

        public string Read(string Key, string Section = null)
        {
            // 2048 chars (not the old 255) so a DPAPI-encrypted, base64-encoded credential
            // never gets silently truncated -- a truncated blob fails to decrypt.
            var RetVal = new StringBuilder(2048);
            GetPrivateProfileString(Section ?? EXE, Key, "", RetVal, 2048, Path);
            return RetVal.ToString();
        }

        public void Write(string Key, string Value, string Section = null)
        {
            WritePrivateProfileString(Section ?? EXE, Key, Value, Path);
        }

        public void DeleteKey(string Key, string Section = null)
        {
            Write(Key, null, Section ?? EXE);
        }

        public void DeleteSection(string Section = null)
        {
            Write(null, null, Section ?? EXE);
        }

        public bool KeyExists(string Key, string Section = null)
        {
            return Read(Key, Section).Length > 0;
        }
    }
}
