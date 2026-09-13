using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // Shared merge algorithm for every "operator-reorderable list of known ids" setting in Jimmy
    // Next (row display order, notification join order, ...). Given the operator's saved order,
    // the current set of valid ids, and the code's own default order, produces a list that:
    //   * keeps 100% of the operator's own relative order for everything they already had,
    //   * drops any saved id that is no longer valid (removed/renamed type),
    //   * and inserts any BRAND NEW id (added to `defaultOrder` in a later release, never seen in
    //     `saved`) immediately next to its nearest neighbor from `defaultOrder` that IS already
    //     present -- preferring the nearest PRECEDING neighbor, falling back to the nearest
    //     FOLLOWING one, so a new category lands where the code's own default says it semantically
    //     belongs RELATIVE TO ITS NEIGHBOR, wherever the operator has moved that neighbor to --
    //     rather than always at the tail regardless of placement, and rather than jumping to
    //     defaultOrder's own absolute index (which could collide with the operator's reordering).
    // A fresh install / a missing or entirely-invalid saved list (`saved` empty or every entry
    // unrecognized) simply returns `defaultOrder` verbatim.
    //
    // See the notification-join-order design writeup for the full worked example this algorithm
    // is built against (moving TargetBusy to the front, then a later release adding a 12th type
    // next to TargetBusy in the code default -- the new type lands right after TargetBusy's
    // CURRENT, operator-chosen position, not at the literal tail and not at TargetBusy's original
    // index).
    public static class OrderedListMigration
    {
        public static List<string> Merge(IEnumerable<string> saved, ICollection<string> validIds,
            IReadOnlyList<string> defaultOrder)
        {
            var validSet = new HashSet<string>(validIds);
            var result = new List<string>();
            if (saved != null)
            {
                foreach (var id in saved)
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!validSet.Contains(id)) continue;
                    if (result.Contains(id)) continue;
                    result.Add(id);
                }
            }

            if (result.Count == 0)
                return new List<string>(defaultOrder);

            foreach (var m in defaultOrder)
            {
                if (result.Contains(m)) continue;

                int defaultIdx = IndexOf(defaultOrder, m);
                string precedingNeighbor = null;
                for (int i = defaultIdx - 1; i >= 0; i--)
                {
                    if (result.Contains(defaultOrder[i])) { precedingNeighbor = defaultOrder[i]; break; }
                }
                if (precedingNeighbor != null)
                {
                    result.Insert(result.IndexOf(precedingNeighbor) + 1, m);
                    continue;
                }

                string followingNeighbor = null;
                for (int i = defaultIdx + 1; i < defaultOrder.Count; i++)
                {
                    if (result.Contains(defaultOrder[i])) { followingNeighbor = defaultOrder[i]; break; }
                }
                if (followingNeighbor != null)
                {
                    result.Insert(result.IndexOf(followingNeighbor), m);
                    continue;
                }

                // No resolvable neighbor at all (result shares nothing with defaultOrder) -- append.
                result.Add(m);
            }

            return result;
        }

        // Typed convenience overload for NotificationEventType (string-name based, same INI
        // serialization convention as callWaitingRowOrder/rawDecodeRowOrder/spotWatchRowOrder).
        public static List<NotificationEventType> Merge(IEnumerable<NotificationEventType> saved,
            ICollection<NotificationEventType> validIds, IReadOnlyList<NotificationEventType> defaultOrder)
        {
            var savedNames = saved?.Select(t => t.ToString());
            var validNames = validIds.Select(t => t.ToString()).ToList();
            var defaultNames = defaultOrder.Select(t => t.ToString()).ToList();
            var mergedNames = Merge(savedNames, validNames, defaultNames);
            var result = new List<NotificationEventType>();
            foreach (var name in mergedNames)
                if (System.Enum.TryParse<NotificationEventType>(name, out var t)) result.Add(t);
            return result;
        }

        private static int IndexOf(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
            return -1;
        }
    }
}
