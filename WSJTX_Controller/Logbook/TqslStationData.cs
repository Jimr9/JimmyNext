using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace WSJTX_Controller
{
    // TQSL's own named Station Locations (2026-10-04), read -- never written -- from its
    // station_data file (an XML <StationDataFile> of <StationData name="..."> entries holding
    // CALL, GRIDSQUARE, DXCC, US_STATE, US_COUNTY, CQZ, ITUZ ...). Used to record, when a contact
    // is logged, which location we were operating from (with a fingerprint of its fields), and to
    // check, before signing, that the location still exists and still says the same thing.
    internal static class TqslStationData
    {
        internal sealed class Location
        {
            public string Name, Call, Grid, State, County, Dxcc, Cqz, Ituz;
            // What the location says, in one comparable line: a change to any of these fields
            // after a contact was logged makes the two differ.
            public string Fingerprint =>
                string.Join("|", Norm(Call), Norm(Grid), Norm(State), Norm(County), Norm(Dxcc), Norm(Cqz), Norm(Ituz));
        }

        private static string Norm(string s) => (s ?? "").Trim().ToUpperInvariant();

        // TQSL's folder: TQSLDIR when set (TQSL honours it), else %APPDATA%\TrustedQSL.
        internal static string FilePath()
        {
            string dir = Environment.GetEnvironmentVariable("TQSLDIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrustedQSL");
            return Path.Combine(dir, "station_data");
        }

        // Every named location, by name (case-insensitive, as TQSL matches -l). Null with `error`
        // when the file is missing or unreadable.
        internal static Dictionary<string, Location> Load(out string error, string path = null)
        {
            error = null;
            path = path ?? FilePath();
            try
            {
                if (!File.Exists(path)) { error = $"TQSL's station location file was not found ({path})."; return null; }
                return Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                error = $"TQSL's station location file could not be read: {ex.Message}";
                return null;
            }
        }

        internal static Dictionary<string, Location> Parse(string xml)
        {
            var result = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
            var doc = XDocument.Parse(xml);
            foreach (var e in doc.Root?.Elements("StationData") ?? new XElement[0])
            {
                string V(string tag) => e.Element(tag)?.Value?.Trim() ?? "";
                var loc = new Location
                {
                    Name = (string)e.Attribute("name") ?? "",
                    Call = V("CALL"), Grid = V("GRIDSQUARE"), State = V("US_STATE"), County = V("US_COUNTY"),
                    Dxcc = V("DXCC"), Cqz = V("CQZ"), Ituz = V("ITUZ"),
                };
                if (loc.Name.Length > 0) result[loc.Name] = loc;
            }
            return result;
        }
    }
}
