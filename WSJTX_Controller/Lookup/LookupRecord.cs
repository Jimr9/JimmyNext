using System;
using System.Collections.Generic;

namespace WSJTX_Controller
{
    // The one common, provider-agnostic record for "what do we know about this
    // callsign". Every lookup provider (QRZ, Club Log, LoTW, DX Spot Watch, and
    // any future service -- eQSL, HamQTH, DX Cluster, Club Log Most Wanted, ...)
    // fills in whichever fields it knows via ILookupProvider.Contribute() and
    // leaves the rest untouched. Consumers (the Lookup dialog, live decode
    // tagging, future award-intelligence answers) read only this record --
    // never a specific provider -- so adding a provider never requires changing
    // a consumer, and adding a consumer never requires knowing which providers
    // exist.
    public class LookupRecord
    {
        public string Callsign     { get; set; }
        public string Name         { get; set; }
        public string LicenseClass { get; set; }
        public string QslManager   { get; set; }
        public string Email        { get; set; }

        public string Country    { get; set; }
        public int    Dxcc       { get; set; }
        public string Continent  { get; set; }
        public int    CqZone     { get; set; }
        public int    ItuZone    { get; set; }
        public string State      { get; set; }
        public string County     { get; set; }
        public string Grid       { get; set; }
        public string Prefix     { get; set; }

        public bool IsDeletedEntity { get; set; }

        public bool      IsLoTWUser       { get; set; }
        public DateTime? LoTWLastActivity { get; set; }

        // Reserved for future providers (e.g. Club Log Most Wanted, an IOTA
        // directory). No current provider populates these -- they stay at
        // their default until one does.
        public string Iota             { get; set; }
        public int?   MostWantedRank   { get; set; }
        public string ActiveDxpedition { get; set; }

        // Most recent reception report from a live spot feed (currently
        // DxSpotWatcher/PSKReporter), for watched calls only.
        public SpotInfo LastSpot { get; set; }

        // Names of every provider that contributed at least one field, in
        // contribution order -- e.g. "QRZ, Club Log, LoTW".
        public List<string> Sources { get; } = new List<string>();

        public string SourcesText => Sources.Count > 0 ? string.Join(", ", Sources) : "No lookup data available";
    }
}
