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
[assembly: AssemblyTitle("Jimmy")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Jimmy")]
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
[assembly: AssemblyFileVersion("1.90.8.0")]      //major.minor.build.private; increment minor when new install pkg
[assembly: AssemblyInformationalVersion("1.90.8")]
