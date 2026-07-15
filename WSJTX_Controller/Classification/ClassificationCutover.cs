using WsjtxUdpLib.Messages.Out;

namespace WSJTX_Controller
{
    // Stage A6 (migration roadmap): emergency rollback valve. INI-only -- read once at
    // startup from an optional, undocumented-to-users "useClassificationEngine" key; not
    // exposed anywhere in OptionsDlg. Default true: queue admission, ranking, display, and
    // award consumers read ClassificationEngine's ClassifiedCall output (computed fresh from
    // Jimmy's own LogbookDb/LookupManager/GeoMath) instead of WSJT-X's wire-supplied
    // EnqueueDecodeMessage classification fields. false: fall back to the original
    // wire-supplied fields exactly as before Stage A6 -- kept available for at least one
    // release in case a real-world edge case surfaces that the replay suite didn't cover.
    public static class ClassificationCutover
    {
        public static bool UseClassificationEngine = true;

        // The ClassifiedCall a consumer should read from for this decode. Both the
        // computed (d.Classified) and wire-supplied (d's own fields) values are always
        // present side by side on the same object regardless of this flag, so a
        // mismatch can always be diagnosed by comparing them directly -- flipping the
        // flag never discards either source.
        public static ClassifiedCall EffectiveClassification(this EnqueueDecodeMessage d)
        {
            if (UseClassificationEngine && d.Classified != null) return d.Classified;

            return new ClassifiedCall
            {
                IsNewCallOnBand = d.IsNewCallOnBand,
                IsNewCallAnyBand = d.IsNewCallAnyBand,
                IsNewCountry = d.IsNewCountry,
                IsNewCountryOnBand = d.IsNewCountryOnBand,
                Country = d.Country,
                Continent = d.Continent,
                IsDx = d.IsDx,
                Azimuth = d.Azimuth,
                Distance = d.Distance,
            };
        }
    }
}
