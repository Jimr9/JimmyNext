using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Item 3 (2026-09-10): the accessible, non-hotkey way into the RX/TX audio-frequency
    // commands. Opened from the Options > Transmit "RX/TX Audio Frequency Controls..." button
    // or an operator-assigned hotkey (HotkeyAction.OpenRxTxFreqControls, no default key).
    //
    // Modeless and single-instance -- Controller.OpenRxTxFreqControlsDialog owns the instance
    // and just brings it forward on a repeat launch. This window holds NO radio state: every
    // control calls one of the existing WsjtxClient methods the F11/F12 family already uses
    // (NudgeTxFrequency / NudgeRxFrequency / SetTxFromRx / SetRxFromTx / SetTxFrequencyHz /
    // SetRxFrequencyHz), so clamping, pending-command handling, engine confirmation, and
    // failure reporting are all the shared paths -- not re-implemented here. A ~500 ms timer
    // refreshes the read-only context and the enabled state without moving focus. Closing the
    // window (Escape or Close) never touches radio state.
    internal class RxTxFreqDlg : Form
    {
        private readonly Controller _ctrl;
        private readonly WsjtxClient _wc;
        private readonly Timer _refresh;

        // Last engine-confirmed RX/TX frequency outcome shown in the status field, and the
        // WsjtxClient result-sequence it came from -- initialised to the current sequence so a
        // result produced before this window opened is not shown as if it just happened.
        private int _lastResultSeq;
        private string _lastResult = "";

        private readonly TextBox _bandBox, _dialBox, _modeBox, _rxBox, _txBox, _behBox, _stepBox, _statusBox;
        private readonly Button _rxDownBtn, _rxUpBtn, _txDownBtn, _txUpBtn, _rxFromTxBtn, _txFromRxBtn;
        private readonly NumericUpDown _txExactUpDown, _rxExactUpDown;
        private readonly Button _txSetBtn, _rxSetBtn, _helpBtn, _closeBtn;

        public RxTxFreqDlg(Controller ctrl, WsjtxClient wc)
        {
            _ctrl = ctrl;
            _wc = wc;
            _lastResultSeq = wc.LastFreqControlResultSeq;

            Text = "RX/TX Audio Frequency Controls";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            ClientSize = new Size(392, 432);
            var font = new Font("Microsoft Sans Serif", 8.25F);
            Font = font;

            int min = _wc.AudioOffsetMinHz, max = _wc.AudioOffsetMaxHz;
            int step = Math.Max(1, _wc.CurrentFreqStepHz);

            // ---- read-only operating context ----------------------------------------------
            _bandBox   = MakeReadOnly("Band", 14);
            _dialBox   = MakeReadOnly("Dial frequency", 41);
            _modeBox   = MakeReadOnly("Mode", 68);
            _rxBox     = MakeReadOnly("Receive audio frequency", 95);
            _txBox     = MakeReadOnly("Transmit audio frequency", 122);
            _behBox    = MakeReadOnly("Transmit frequency behavior", 149);
            _stepBox   = MakeReadOnly("Frequency step", 176);
            AddLabel("Band:", 14);
            AddLabel("Dial frequency:", 41);
            AddLabel("Mode:", 68);
            AddLabel("Receive audio frequency:", 95);
            AddLabel("Transmit audio frequency:", 122);
            AddLabel("Transmit frequency behavior:", 149);
            AddLabel("Frequency step:", 176);

            // ---- step controls -----------------------------------------------------------
            _rxDownBtn   = MakeButton("Receive frequency down", 12, 210, 185, 26, () => { _wc.NudgeRxFrequency(-1); RefreshNow(); });
            _rxUpBtn     = MakeButton("Receive frequency up", 203, 210, 185, 26, () => { _wc.NudgeRxFrequency(+1); RefreshNow(); });
            _txDownBtn   = MakeButton("Transmit frequency down", 12, 240, 185, 26, () => { _wc.NudgeTxFrequency(-1); RefreshNow(); });
            _txUpBtn     = MakeButton("Transmit frequency up", 203, 240, 185, 26, () => { _wc.NudgeTxFrequency(+1); RefreshNow(); });
            _rxFromTxBtn = MakeButton("Set receive frequency to transmit", 12, 270, 185, 26, () => { _wc.SetRxFromTx(); RefreshNow(); });
            _txFromRxBtn = MakeButton("Set transmit frequency to receive", 203, 270, 185, 26, () => { _wc.SetTxFromRx(); RefreshNow(); });

            // ---- exact type-in (thin wrappers over the same setters) --------------------
            AddLabel("Exact transmit hertz:", 306);
            _txExactUpDown = new NumericUpDown
            {
                Location = new Point(150, 303), Size = new Size(80, 20),
                Minimum = min, Maximum = max, Increment = step,
                Value = Math.Max(min, Math.Min(max, _wc.CurrentTxOffsetHz > 0 ? _wc.CurrentTxOffsetHz : 1500)),
                AccessibleName = "Exact transmit frequency in hertz", TabIndex = 13,
            };
            Controls.Add(_txExactUpDown);
            _txSetBtn = MakeButton("Set transmit frequency", 240, 302, 140, 23,
                () => { _wc.SetTxFrequencyHz((int)_txExactUpDown.Value); RefreshNow(); });
            _txSetBtn.TabIndex = 14;

            AddLabel("Exact receive hertz:", 336);
            _rxExactUpDown = new NumericUpDown
            {
                Location = new Point(150, 333), Size = new Size(80, 20),
                Minimum = min, Maximum = max, Increment = step,
                Value = Math.Max(min, Math.Min(max, _wc.CurrentRxOffsetHz > 0 ? _wc.CurrentRxOffsetHz : 1500)),
                AccessibleName = "Exact receive frequency in hertz", TabIndex = 15,
            };
            Controls.Add(_rxExactUpDown);
            _rxSetBtn = MakeButton("Set receive frequency", 240, 332, 140, 23,
                () => { _wc.SetRxFrequencyHz((int)_rxExactUpDown.Value); RefreshNow(); });
            _rxSetBtn.TabIndex = 16;

            // ---- status + window buttons ----------------------------------------------------
            _statusBox = new TextBox
            {
                Location = new Point(12, 366), Size = new Size(368, 20),
                ReadOnly = true, TabStop = true, TabIndex = 17,
                AccessibleName = "Status",
            };
            Controls.Add(_statusBox);

            _helpBtn = MakeButton("Help", 12, 396, 90, 26, OpenHelp);
            _helpBtn.AccessibleName = "Help, opens the Jimmy website";
            _helpBtn.TabIndex = 18;
            _closeBtn = MakeButton("Close", 290, 396, 90, 26, Close);
            _closeBtn.AccessibleName = "Close";
            _closeBtn.TabIndex = 19;
            CancelButton = _closeBtn;

            // Give the context fields a stable leading tab order (0..6).
            _bandBox.TabIndex = 0; _dialBox.TabIndex = 1; _modeBox.TabIndex = 2;
            _rxBox.TabIndex = 3; _txBox.TabIndex = 4; _behBox.TabIndex = 5; _stepBox.TabIndex = 6;
            _rxDownBtn.TabIndex = 7; _rxUpBtn.TabIndex = 8;
            _txDownBtn.TabIndex = 9; _txUpBtn.TabIndex = 10;
            _rxFromTxBtn.TabIndex = 11; _txFromRxBtn.TabIndex = 12;

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; e.SuppressKeyPress = true; Close(); }
            };
            FormClosed += (s, e) => { _refresh.Stop(); _refresh.Dispose(); };

            _refresh = new Timer { Interval = 500 };
            _refresh.Tick += (s, e) => RefreshNow();
            Load += (s, e) =>
            {
                try
                {
                    Screen screen = Screen.FromControl(_ctrl);
                    Location = new Point(
                        screen.Bounds.X + (screen.Bounds.Width - Width) / 2,
                        screen.Bounds.Y + (screen.Bounds.Height - Height) / 2);
                }
                catch { }
                RefreshNow();
                _refresh.Start();
            };
        }

        private TextBox MakeReadOnly(string accessibleName, int y)
        {
            var b = new TextBox
            {
                Location = new Point(190, y - 3), Size = new Size(190, 20),
                ReadOnly = true, TabStop = true,
                AccessibleName = accessibleName,
            };
            Controls.Add(b);
            return b;
        }

        private void AddLabel(string text, int y)
        {
            Controls.Add(new Label { Text = text, Location = new Point(12, y), AutoSize = true, TabStop = false });
        }

        private Button MakeButton(string text, int x, int y, int w, int h, Action onClick)
        {
            var b = new Button
            {
                Text = text, AccessibleName = text,
                Location = new Point(x, y), Size = new Size(w, h),
            };
            b.Click += (s, e) => onClick();
            Controls.Add(b);
            return b;
        }

        private static string BehaviorText(WsjtxClient.TxFreqMode m)
        {
            switch (m)
            {
                case WsjtxClient.TxFreqMode.BestFree:  return "Best free";
                case WsjtxClient.TxFreqMode.OnStation: return "On station";
                default:                               return "Hold";
            }
        }

        // Pull the live operating context and re-gate the controls. Only writes a field when
        // its text actually changed, so a screen reader is not nudged on every 500 ms tick.
        private void RefreshNow()
        {
            if (IsDisposed || Disposing) return;

            bool engineUp = _wc.ConnectedToWsjtx();
            ulong dialHz = _wc.CurrentDialFrequencyHz;
            bool bandKnown = dialHz > 0;
            bool ready = engineUp && bandKnown;
            bool txBusy = _wc.TxFrequencyChangeInFlight;
            bool rxBusy = _wc.RxFrequencyChangeInFlight;

            SetText(_bandBox, bandKnown ? _wc.CurrentBandStr : "Unknown");
            SetText(_dialBox, bandKnown
                ? (dialHz / 1e6).ToString("F3", CultureInfo.InvariantCulture) + " MHz"
                : "Unknown");
            string mode = _wc.CurrentMode;
            SetText(_modeBox, string.IsNullOrEmpty(mode) ? "Unknown" : mode);
            int rx = _wc.CurrentRxOffsetHz, tx = _wc.CurrentTxOffsetHz;
            SetText(_rxBox, rx > 0 ? rx + " Hz" : "Unknown");
            SetText(_txBox, tx > 0 ? tx + " Hz" : "Unknown");
            SetText(_behBox, BehaviorText(_wc.TxFrequencyMode));
            SetText(_stepBox, _wc.CurrentFreqStepHz + " Hz");

            int stepInc = Math.Max(1, _wc.CurrentFreqStepHz);
            if (_txExactUpDown.Increment != stepInc) _txExactUpDown.Increment = stepInc;
            if (_rxExactUpDown.Increment != stepInc) _rxExactUpDown.Increment = stepInc;

            bool rxOk = ready && !rxBusy;
            bool txOk = ready && !txBusy;
            _rxDownBtn.Enabled = _rxUpBtn.Enabled = _rxFromTxBtn.Enabled =
                _rxExactUpDown.Enabled = _rxSetBtn.Enabled = rxOk;
            _txDownBtn.Enabled = _txUpBtn.Enabled = _txFromRxBtn.Enabled =
                _txExactUpDown.Enabled = _txSetBtn.Enabled = txOk;

            // Mirror the engine-confirmed RX/TX result (or the honest failure) that
            // ApplyManualTx/RxOffset already produced -- captured here, not re-derived. A newer
            // result supersedes the previous one; the honest engine/band/in-flight states still
            // take precedence when they apply.
            int resultSeq = _wc.LastFreqControlResultSeq;
            if (resultSeq != _lastResultSeq)
            {
                _lastResultSeq = resultSeq;
                _lastResult = _wc.LastFreqControlResult ?? "";
            }

            string status =
                !engineUp ? "Engine not available. Frequency controls are disabled." :
                !bandKnown ? "Band not known yet. Frequency controls are disabled." :
                (txBusy || rxBusy) ? "Applying a change; waiting for the engine to confirm." :
                !string.IsNullOrEmpty(_lastResult) ? _lastResult :
                "Ready.";
            SetText(_statusBox, status);
        }

        private static void SetText(TextBox box, string text)
        {
            if (box.Text != text) box.Text = text;
        }

        // Test-only hook (JimmyTests, InternalsVisibleTo): run one refresh pass without
        // showing the window or starting the 500 ms timer.
        internal void RefreshStatusForTest() => RefreshNow();

        private void OpenHelp()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://blindsea.com/jimmy20") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
