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

        // A location in words, for a person: "callsign KB0UZT, grid EN34RN, state MN, county
        // Goodhue, DXCC 291, CQ zone 4, ITU zone 7". Empty parts are left out.
        internal static string Describe(Location l)
        {
            var parts = new List<string>();
            void Add(string label, string v) { if (!string.IsNullOrWhiteSpace(v)) parts.Add(label + " " + v.Trim()); }
            Add("callsign", l.Call); Add("grid", l.Grid); Add("state", l.State); Add("county", l.County);
            Add("DXCC", l.Dxcc); Add("CQ zone", l.Cqz); Add("ITU zone", l.Ituz);
            return parts.Count == 0 ? "no details" : string.Join(", ", parts);
        }

        // What a contact's recorded TQSL location is worth now (2026-10-12). One answer for the
        // contact editor and the LoTW upload, so they never disagree:
        //   Legacy       nothing was recorded (logged before locations were recorded): signed
        //                with the location set in Options, as always.
        //   NoneSet      logged while no location was set in Options.
        //   NotCaptured  a location was named, but TQSL did not have it at the time, so what it
        //                said was not recorded. NOT a changed location.
        //   NoDetails    a location was named and its details were never recorded at all.
        //   Unreadable   TQSL's station data cannot be read now.
        //   Gone         the named location is not in TQSL now.
        //   WrongCall    the location's callsign is not the contact's station callsign.
        //   Changed      the location says something else now than when the contact was logged.
        //   Ok           recorded, still there, still says the same.
        internal enum Recorded { Legacy, NoneSet, NotCaptured, NoDetails, Unreadable, Gone, WrongCall, Changed, Ok }

        internal static Recorded Judge(string location, string fingerprint, string stationCall, Dictionary<string, Location> locations)
        {
            string rec = (location ?? "").Trim(), fp = (fingerprint ?? "").Trim();
            if (rec.Length == 0) return Recorded.Legacy;
            if (rec == Controller.TqslNoLocation) return Recorded.NoneSet;
            if (locations == null) return Recorded.Unreadable;
            if (!locations.TryGetValue(rec, out var loc)) return Recorded.Gone;
            if (loc.Call.Length > 0 && !string.Equals(loc.Call, (stationCall ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return Recorded.WrongCall;
            if (fp == Controller.TqslLocationMissing) return Recorded.NotCaptured;
            if (fp.Length == 0) return Recorded.NoDetails;
            return string.Equals(loc.Fingerprint, fp, StringComparison.Ordinal) ? Recorded.Ok : Recorded.Changed;
        }

        internal const string RepairHint = "open the contact in Logbook, check Allow editing and Allow editing protected fields, and use Choose TQSL Location";

        // Why a contact is held from LoTW signing, or null when it can be signed.
        internal static string HeldWhy(Recorded r, string location, string stationCall, Dictionary<string, Location> locations, string loadError)
        {
            string rec = (location ?? "").Trim();
            switch (r)
            {
                case Recorded.Legacy: case Recorded.Ok: return null;
                case Recorded.NoneSet: return "logged with no TQSL station location set; " + RepairHint;
                case Recorded.Unreadable: return $"TQSL station data could not be read ({loadError?.TrimEnd('.')})";
                case Recorded.Gone: return $"TQSL station location '{rec}' it was logged with no longer exists; create it again in TQSL, or " + RepairHint;
                case Recorded.WrongCall:
                    return $"station callsign {stationCall} is not the callsign of TQSL location '{rec}' ({(locations != null && locations.TryGetValue(rec, out var l) ? l.Call : "?")})";
                case Recorded.NotCaptured:
                    return $"TQSL station location '{rec}' was not available in TQSL when these contacts were logged, so what it said was not recorded and needs your review; " + RepairHint;
                case Recorded.NoDetails:
                    return $"the details of TQSL station location '{rec}' were not recorded with these contacts and need your review; " + RepairHint;
                default:
                    return $"TQSL station location '{rec}' was changed in TQSL after these contacts were logged; if they were made from the location as it is now, " + RepairHint;
            }
        }

        // The same, for one contact in the editor: a sentence the screen reader reads.
        internal static string StatusSentence(Recorded r, string location)
        {
            string rec = (location ?? "").Trim();
            switch (r)
            {
                case Recorded.Legacy: return "No TQSL location was recorded with this contact. It is signed with the location set in Options.";
                case Recorded.Ok: return $"Location '{rec}' is recorded, and TQSL still has it unchanged.";
                case Recorded.NoneSet: return "Logged with no TQSL location set. Held from LoTW upload until one is chosen.";
                case Recorded.Unreadable: return "TQSL's station locations cannot be read on this computer now.";
                case Recorded.Gone: return $"Location '{rec}' is not in TQSL now. Held from LoTW upload.";
                case Recorded.WrongCall: return $"Location '{rec}' is for another callsign. Held from LoTW upload.";
                case Recorded.NotCaptured: return $"Location '{rec}' was not available when this contact was logged, so its details were not recorded. Held from LoTW upload until reviewed.";
                case Recorded.NoDetails: return $"Location '{rec}' is named, but its details were not recorded. Held from LoTW upload until reviewed.";
                default: return $"Location '{rec}' was changed in TQSL after this contact was logged. Held from LoTW upload until reviewed.";
            }
        }

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
