using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // Stage A6 test infrastructure. Deterministic, hand-authored, version-controlled
    // lookup data for the specific callsigns JimmyReplay.py's replay scenarios use as
    // test fixtures (see JimmyReplay.py's *_CALL constants and build_enqueue() calls --
    // this table's Country/Continent values are chosen to match those exactly, so a
    // reader can trace each entry back to the scenario that exercises it).
    //
    // Makes zero network or disk I/O of any kind (a plain in-memory dictionary), so it
    // carries none of the real-provider risk TestModeGuard.cs exists to prevent. It must
    // still never be wired into a real user session -- it would silently override real
    // Country/Continent/DXCC/Grid data with fake test values. The only two places that
    // may construct/register this are JimmyTests.cs's own parity tests and (if ever
    // needed for live-replay wiring) a TestModeGuard.IsTestMode-gated startup branch --
    // verified by inspection, not a runtime self-check, since unlike QRZ/ClubLog/LoTW/
    // FccUls there is no live call here to guard against.
    //
    // Grid is populated even for calls whose own decode messages already carry a grid
    // (e.g. a CQ ending in a Maidenhead square) -- ClassificationEngine tries the
    // message's own grid first and only falls back to this, so having both lets the
    // same call's grid-carrying and grid-less messages (e.g. a CQ vs. a later signal
    // report) resolve to the identical, consistent location.
    public class TestFixtureLookupProvider : ILookupProvider
    {
        public string SourceName => "TestFixture";
        public bool IsEnabled => true;

        public class Fixture
        {
            public string Country;
            public string Continent;
            public int Dxcc;
            public string Grid;
        }

        // W5C/H (SLASH_CALL, JimmyReplay.py group 7) is intentionally absent -- its
        // replay fixture explicitly passes country=None/continent=None to test the
        // unresolvable-call path, so this provider must also resolve nothing for it.
        public static readonly IReadOnlyDictionary<string, Fixture> Fixtures =
            new Dictionary<string, Fixture>(StringComparer.OrdinalIgnoreCase)
        {
            // THEIR_CALL -- groups 1,3,5,6,9 (T01-T06,T09-T10,T15,T18)
            ["K4YT"]    = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // FT8_EVEN_CALL/FT8_ODD_CALL -- group 10 (T19-T20)
            ["W9EVN"]   = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            ["W9ODD"]   = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // SOTA_CALL -- group 8 (T17)
            ["W0SDT"]   = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // FH_CALL -- group 11 (T21-T24)
            ["K1ABC/H"] = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // HRC_US_CALL/HRC_DX_CALL -- group 12 (T25-T26)
            ["W5HRC"]   = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM10" },
            ["G3HRC"]   = new Fixture { Country = "England", Continent = "EU", Dxcc = 223, Grid = "IO91" },
            // SNL_US_CALL/SNL_DX_CALL -- group 13 (T27-T28)
            ["K5SNL"]   = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "DM79" },
            ["PY5SNL"]  = new Fixture { Country = "Brazil", Continent = "SA", Dxcc = 108, Grid = "GG66" },
            // WAIT_CALL -- group 15 (T32-T35)
            ["W3WAIT"]  = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // RRR_CALL -- group 16 (T36-T37)
            ["W4RRR"]   = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // NEEDED_CALL -- group 17 (T38-T39)
            ["W9NEED"]  = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // WEAK_CALL -- group 18 (T40-T41)
            ["W5WEAK"]  = new Fixture { Country = "USA",    Continent = "NA", Dxcc = 291, Grid = "EM63" },
            // Not a JimmyReplay.py fixture -- added to catch the exact bug class found via
            // live A6 field testing 2026-07-16: real lookup providers return their own raw
            // country strings, not WSJT-X's normalized set. "United States" mirrors QRZ's
            // actual raw output (Club Log returns "UNITED STATES OF AMERICA"); Classify()
            // must normalize this to "USA" via EnqueueDecodeMessage.WsjtxCountry(), same as
            // the wire-supplied Country setter always did.
            ["K3ZK"]    = new Fixture { Country = "United States", Continent = "NA", Dxcc = 291, Grid = "FN21" },
        };

        public void Contribute(LookupRecord record, string call)
        {
            if (string.IsNullOrEmpty(call)) return;
            if (!Fixtures.TryGetValue(call, out Fixture fx)) return;

            if (string.IsNullOrEmpty(record.Country))   record.Country   = fx.Country;
            if (string.IsNullOrEmpty(record.Continent)) record.Continent = fx.Continent;
            if (record.Dxcc == 0)                       record.Dxcc      = fx.Dxcc;
            if (string.IsNullOrEmpty(record.Grid))      record.Grid      = fx.Grid;
            record.Sources.Add(SourceName);
        }
    }
}
