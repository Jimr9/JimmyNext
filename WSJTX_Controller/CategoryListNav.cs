using System.Collections.Generic;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The accessible "one category list, one visible page" navigation mechanism shared by
    // Options (OptionsDlg._categoryListBox/_categoryDetailHost), Logbook Center
    // (LogbookWindow._categoryListBox/_categoryDetailHost), and Contesting
    // (ContestingWindow._categoryListBox/_categoryDetailHost) -- a plain ListBox driving exactly
    // one visible page at a time, proven with real JAWS/NVDA testing in Options first. Extracted
    // here once three near-identical copies of the same ~10-line method existed, rather than
    // adding a fourth.
    //
    // Adds/Removes the active page instead of toggling Visible: live JAWS testing (Options,
    // 2026-08-10; Contesting, 2026-09-25) found that a hidden-but-still-parented sibling page can
    // still bleed stale content into what JAWS announces for the newly selected one -- its
    // accessibility cache does not reliably invalidate for a merely-hidden sibling still present
    // in the tree. Only ever parenting the ONE currently-selected page avoids that class of bug
    // entirely, and is also what makes it safe for each page to carry its own real
    // AccessibleName (see each caller's own page-panel setup).
    public static class CategoryListNav
    {
        public static void Wire(ListBox listBox, Control host, List<Control> panels)
        {
            Control current = null;
            void UpdateVisibility()
            {
                if (current != null) host.Controls.Remove(current);
                int idx = listBox.SelectedIndex;
                current = (idx >= 0 && idx < panels.Count) ? panels[idx] : null;
                if (current != null) host.Controls.Add(current);
            }
            listBox.SelectedIndexChanged += (s, e) => UpdateVisibility();
            if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
            UpdateVisibility();
        }
    }
}
