using System;
using System.Threading.Tasks;

namespace WSJTX_Controller
{
    // The subset of QrzProvider's public shape that any interchangeable "primary online
    // callsign lookup provider" needs -- lets LookupManager's automatic-lookup/policy/queue
    // machinery (built for QRZ) operate against whichever provider the operator selects
    // (QRZ or HamQTH) without duplicating that machinery per provider. Both QrzProvider and
    // HamQthProvider already had this exact shape; this interface just names it.
    public interface IOnlineLookupProvider : ILookupProvider
    {
        bool NeedsLookup(string call);
        Task<LookupRecord> LookupAsync(string call);
        DateTime? GetCachedAt(string call);
        Task<bool> TestAsync();
        string LastError { get; }
    }
}
