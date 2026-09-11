using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Note: List<string>, not IReadOnlyList<string> -- matches exactly what WsjtxClient
    // already builds in memory before touching the UI, avoiding a pointless conversion.
    // View seams (Phase 2.3 of the modernization plan) letting WsjtxClient stop holding a raw
    // `public Controller ctrl` reference. Controller implements these; WsjtxClient is being
    // migrated call-site-by-call-site onto them (Phase 2.4), one bounded, well-understood
    // render method at a time. The Advanced Call Layout TX1/TX2 snapshot lists (distinct from
    // Raw Decodes) are deliberately left on the old direct-ctrl path for now -- they carry
    // extra period/snapshot bookkeeping that deserves its own dedicated wave.

    public interface IJimmyStatusView
    {
        // Item 1/2 split (2026-09-03): the visible status line + Notification History entry ONLY.
        // Both immediate and unconditional -- NEVER gated on whether the line will be spoken.
        // Returns whether Jimmy is really foregrounded/focused right now (statusText.Focused &&
        // ActiveForm == this && GetForegroundWindow() == handle) -- the caller passes that to
        // SpeechCoordinator.SubmitRoutineStatus as the "would this have nudged the screen reader"
        // hint. The screen-reader nudge itself is CoordinatedSpeak below, driven by the coordinator.
        //
        // isReceiveCycleSummaryRender (2026-09-10): true only for the idle Receive-cycle-summary
        // render (WsjtxClient.ShowStatus's callInProg==null/deferEligible branch), false for
        // every other kind of status (QSO/CAT/error/upload/Smart Start/Station Watch, or a direct
        // operator-feedback message). Lets the implementation safely clear a STALE summary line
        // when Options > Notifications > "Clear previous summary when it becomes empty" is on --
        // see Controller.RenderStatusVisible's own comment for the full eligibility rule. Default
        // false so every other call site (including every existing test) is unaffected.
        bool RenderStatusVisible(string headingText, string statusText, Color foreColor, Color backColor,
            bool isReceiveCycleSummaryRender = false);

        // The ONE screen-reader nudge seam, driven only by SpeechCoordinator -- for BOTH a
        // coordinated typed notification and a coordinated routine-status line. Idempotently
        // ensures statusText shows `text`, applies the real OS-foreground guard and the 3-second
        // exact/near-immediate duplicate suppression, then SendKeys("{UP}"). Records NO history
        // (that already happened upstream, at render / deliver time). Never self-voices, never
        // moves focus, never calls a screen-reader API directly.
        void CoordinatedSpeak(string text);

        // Wraps the existing (currently no-op) Controller.ShowMsg -- direct one-shot operator
        // feedback (hotkey results, upload status, "not in queue", ...). NOT the routine-status
        // or typed-notification path; those go through CoordinatedSpeak via the coordinator.
        void ShowMessage(string text, bool sound);

        // Added 2026-08-19 for the off-focus accessibility-alert feature (UiaAlertNotification
        // Delivery, WSJTX_Controller/Notify/NotificationDelivery.cs). Whether announcing RIGHT
        // NOW via ShowMessage would actually be heard -- statusText focused AND this is the
        // active form, mirroring Controller.ShowMsg's own internal `announced` check exactly.
        // Lets a delivery layer outside Controller ask "will the normal path already say this?"
        // before raising a second, redundant announcement through a different channel for the
        // same event.
        bool WouldAnnounce { get; }

        // Announces text via UI Automation's Notification event (System.Windows.Forms.
        // AccessibleObject.RaiseAutomationNotification) without moving keyboard focus and
        // without touching statusText's own displayed text -- see UiaAlertNotificationDelivery's
        // own comment for the full design and why this exists. Must never throw (an AT that
        // doesn't support UIA notifications, or any other failure here, is a silent no-op, never
        // a crash or a fallback to anything that WOULD move focus/self-voice).
        void RaiseAccessibleAlert(string text);
    }

    public interface IJimmyQueueView
    {
        // Mirrors WsjtxClient.ShowQueue()'s list-rendering tail: change-detection, focus/selection
        // preservation, and BeginUpdate/EndUpdate batching. Queue-index bookkeeping
        // (_callListBoxQueueIndices) stays in WsjtxClient -- it's queue state, not view state.
        // `keys` is parallel to `items` (same order/count) and identifies each row's station so
        // selection can be preserved by identity across a rebuild instead of by raw position --
        // see Controller.FindPreservedSelectionIndex(). `categories` is also parallel to `items`
        // -- each row's CallCategory, used only to pick a per-category alert color in
        // Controller.AdvListBox_DrawItem (Options > Appearance).
        void RenderCallQueue(string headerText, List<string> items, List<string> keys, List<WsjtxClient.CallCategory> categories, SelectionMode selectionMode);

        // Mirrors WsjtxClient.ShowRawDecodes()'s list-rendering tail (advRawListBox). No header
        // label update here -- the raw decodes panel has none, unlike the call queue. `keys` is
        // parallel to `items`; since the same callsign can appear in several distinct rows (CQ,
        // reply, report, ...), each key must disambiguate the specific decode, not just the call.
        // `categories` is parallel to `items` (see RenderCallQueue).
        void RenderRawDecodes(List<string> items, List<string> keys, List<WsjtxClient.CallCategory> categories);

        // Mirrors WsjtxClient.ShowAdvancedQueue()'s per-side tail (advTx1ListBox/advTx2ListBox):
        // AccessibleName update (only when the call count actually changed), then the same
        // change-detection + BeginUpdate/EndUpdate + focus/selection-preservation shape as the
        // other Render* methods here. `keys` is parallel to `items` (the callsign for each row).
        // `categories` is parallel to `items` (see RenderCallQueue).
        void RenderAdvancedList(bool isTx1Side, string accessibleName, List<string> items, List<string> keys, List<WsjtxClient.CallCategory> categories);
    }

    public interface IJimmyLogView
    {
        // Mirrors WsjtxClient.ShowLogged()'s list-rendering tail (same shape as RenderCallQueue,
        // without the queue-index bookkeeping since the logged list has no queue positions).
        // `keys` is parallel to `items` (the callsign for each row).
        void RenderLoggedList(string headerText, List<string> items, List<string> keys);
    }
}
