using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// net10.0-windows SDK-style projects normally get this attribute auto-generated
// (tied to GenerateAssemblyInfo, which is off here to avoid clashing with the
// attributes below). Without it, the CA1416 platform-compatibility analyzer treats
// every WinForms call site as "reachable on all platforms" instead of recognizing
// this as a Windows-only build.
[assembly: SupportedOSPlatform("windows")]

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
// Title/Product deliberately mirror the AssemblyName override used to build a side-by-side
// "Jimmy Next" flavor (build.bat, Setup_WiX\JimmyNext.wxs both pass
// -p:AssemblyName="Jimmy Next"), via JIMMY_NEXT_BUILD (set by Jimmy.csproj from that same
// property -- see its own comment). Production safety fix, 2026-08-13: these two attributes
// were hardcoded to "Jimmy" regardless of AssemblyName, so a Jimmy Next build's File Properties
// dialog and Application.ProductName both claimed to BE "Jimmy" even while running as a
// correctly-isolated "Jimmy Next.exe" -- misleading at minimum, and Application.ProductName
// specifically feeds .NET's legacy Properties.Settings.Default user.config path, one more
// reason that identity has to actually vary by build, not just the file name.
#if JIMMY_NEXT_BUILD
[assembly: AssemblyTitle("Jimmy Next")]
[assembly: AssemblyProduct("Jimmy Next")]
#else
[assembly: AssemblyTitle("Jimmy")]
[assembly: AssemblyProduct("Jimmy")]
#endif
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("Copyright ©  2025")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("da3afa58-c818-48b7-83ec-9ff1fc17e4dd")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.0.0.0")]          //major.minor.build.private; freeze all so config re-used each new AssemblyFileVersion
[assembly: AssemblyFileVersion("2.0.39.0")]      //major.minor.build.private; increment minor when new install pkg
[assembly: AssemblyInformationalVersion("2.0.39")]
