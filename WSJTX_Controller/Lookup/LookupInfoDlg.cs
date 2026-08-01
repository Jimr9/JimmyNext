using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Accessible dialog showing all available lookup data for a callsign.
    // Built entirely in code; no Designer.cs file.
    public class LookupInfoDlg : Form
    {
        private readonly TextBox _statusValue;
        private readonly Button  _closeButton;

        // Rows: (label, read-only value textbox — must be focusable so JAWS/NVDA
        // users can reach each field with Tab; a plain Label can never take focus).
        private readonly TextBox _callValue, _nameValue, _licClassValue, _gridValue, _stateValue,
                                 _countryValue, _continentValue, _countyValue, _cqzoneValue,
                                 _ituzoneValue, _adifValue, _qslManagerValue, _emailValue,
                                 _lotwValue, _activityValue, _sourcesValue;

        private readonly LookupManager _manager;
        private readonly string        _call;
        private bool _canQrz;

        public bool QrzLookupOccurred { get; private set; }

        public LookupInfoDlg(string call, LookupManager manager)
        {
            _call    = call?.ToUpperInvariant() ?? "";
            _manager = manager;

            Text            = $"Station Lookup — {_call}";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            StartPosition   = FormStartPosition.CenterParent;
            Size            = new Size(480, 510);
            Font            = new Font("Microsoft Sans Serif", 9F);
            KeyPreview      = true;
            KeyDown        += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            int lx = 16, vx = 160, y = 14, rh = 24, fw = 300, tabIndex = 0;

            _callValue      = AddRow("Callsign:",        ref y, lx, vx, fw, rh, ref tabIndex);
            _nameValue      = AddRow("Name:",            ref y, lx, vx, fw, rh, ref tabIndex);
            _licClassValue  = AddRow("License Class:",   ref y, lx, vx, fw, rh, ref tabIndex);
            _gridValue      = AddRow("Grid:",            ref y, lx, vx, fw, rh, ref tabIndex);
            _stateValue     = AddRow("State/Province:",  ref y, lx, vx, fw, rh, ref tabIndex);
            _countryValue   = AddRow("Country:",         ref y, lx, vx, fw, rh, ref tabIndex);
            _continentValue = AddRow("Continent:",       ref y, lx, vx, fw, rh, ref tabIndex);
            _countyValue    = AddRow("County:",          ref y, lx, vx, fw, rh, ref tabIndex);
            _cqzoneValue    = AddRow("CQ Zone:",         ref y, lx, vx, fw, rh, ref tabIndex);
            _ituzoneValue   = AddRow("ITU Zone:",        ref y, lx, vx, fw, rh, ref tabIndex);
            _adifValue      = AddRow("ADIF Entity:",     ref y, lx, vx, fw, rh, ref tabIndex);
            _qslManagerValue= AddRow("QSL Manager:",     ref y, lx, vx, fw, rh, ref tabIndex);
            _emailValue     = AddRow("Email:",           ref y, lx, vx, fw, rh, ref tabIndex);
            _lotwValue      = AddRow("LoTW user:",       ref y, lx, vx, fw, rh, ref tabIndex);
            _activityValue  = AddRow("LoTW last upload:",ref y, lx, vx, fw, rh, ref tabIndex);

            y += 4;
            var sepLine = new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(lx, y), Size = new Size(fw + vx - lx, 2), TabStop = false };
            Controls.Add(sepLine);
            y += 8;

            _sourcesValue = AddRow("Sources:", ref y, lx, vx, fw, rh, ref tabIndex);

            _statusValue = new TextBox
            {
                Location       = new Point(lx, y + 6),
                Size           = new Size(fw + vx - lx, 18),
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                TabIndex       = tabIndex++,
                ForeColor      = Color.DimGray,
                AccessibleName = "Lookup status",
            };
            Controls.Add(_statusValue);

            y += 32;

            _closeButton = new Button
            {
                Text           = "Close",
                Location       = new Point(fw + vx - 70, y),
                Size           = new Size(70, 26),
                TabIndex       = tabIndex++,
                DialogResult   = DialogResult.OK,
                AccessibleName = "Close",
            };
            Controls.Add(_closeButton);
            AcceptButton = _closeButton;

            PopulateFromCache();

            // Opening this dialog (via the lookup hotkey) is itself the explicit,
            // user-initiated request that FocusedOnly/UnidentifiedQueue policies are
            // gated on -- so a needed QRZ lookup fires automatically; there's no
            // separate button for it any more.
            Load += async (s, e) =>
            {
                if (!_canQrz)
                {
                    _statusValue.Text = "QRZ lookup disabled.";
                }
                else if (_manager.QrzNeedsLookup(_call))
                {
                    await DoQrzLookupAsync();
                }
                else
                {
                    var cachedAt = _manager.QrzCachedAt(_call);
                    _statusValue.Text = cachedAt.HasValue
                        ? $"QRZ data from {FormatAge(cachedAt.Value)}."
                        : "Using cached data.";
                }
            };
        }

        private TextBox AddRow(string labelText, ref int y, int lx, int vx, int fw, int rh, ref int tabIndex)
        {
            var lbl = new Label
            {
                Text      = labelText,
                Location  = new Point(lx, y + 3),
                Size      = new Size(vx - lx - 4, rh - 4),
                TabStop   = false,
            };
            var val = new TextBox
            {
                Location       = new Point(vx, y + 1),
                Size           = new Size(fw, rh - 4),
                ReadOnly       = true,
                BorderStyle    = BorderStyle.None,
                BackColor      = SystemColors.Control,
                TabIndex       = tabIndex++,
                AccessibleName = labelText.TrimEnd(':'),
            };
            Controls.Add(lbl);
            Controls.Add(val);
            y += rh;
            return val;
        }

        private void PopulateFromCache()
        {
            if (_manager == null) { ShowNoData(); return; }

            var info = _manager.GetInfoForDialog(_call);
            if (info == null) { ShowNoData(); return; }

            _callValue.Text      = info.Callsign  ?? _call;
            _nameValue.Text      = info.Name       ?? "—";
            _licClassValue.Text  = FormatLicenseClass(info.LicenseClass);
            _gridValue.Text      = info.Grid       ?? "—";
            _stateValue.Text     = info.State      ?? "—";
            _countryValue.Text   = info.Country    ?? "—";
            _continentValue.Text = info.Continent  ?? "—";
            _countyValue.Text    = info.County     ?? "—";
            _cqzoneValue.Text    = info.CqZone > 0 ? info.CqZone.ToString() : "—";
            _ituzoneValue.Text   = info.ItuZone > 0 ? info.ItuZone.ToString() : "—";
            _adifValue.Text      = info.Dxcc > 0 ? info.Dxcc.ToString() : "—";
            _qslManagerValue.Text= info.QslManager ?? "—";
            _emailValue.Text     = info.Email      ?? "—";
            _lotwValue.Text      = info.IsLoTWUser ? "Yes" : ((_manager.LoTW.IsEnabled && _manager.LoTW.UserCount > 0) ? "No" : "—");
            _activityValue.Text  = info.LoTWLastActivity.HasValue
                                   ? info.LoTWLastActivity.Value.ToLocalTime().ToString("d")
                                   : "—";
            _sourcesValue.Text   = info.SourcesText;

            _canQrz = _manager.Qrz.IsEnabled &&
                      (_manager.Policy == QrzLookupPolicy.FocusedOnly ||
                       _manager.Policy == QrzLookupPolicy.UnidentifiedQueue);
        }

        private void ShowNoData()
        {
            _callValue.Text    = _call;
            foreach (var lbl in new[] { _nameValue, _licClassValue, _gridValue, _stateValue, _countryValue,
                                        _continentValue, _countyValue, _cqzoneValue, _ituzoneValue,
                                        _adifValue, _qslManagerValue, _emailValue,
                                        _lotwValue, _activityValue, _sourcesValue })
                lbl.Text = "—";
        }

        private async Task DoQrzLookupAsync()
        {
            if (_manager == null) return;
            _statusValue.Text = "Looking up via QRZ…";
            var result = await _manager.LookupQrzAsync(_call);
            if (IsDisposed) return;
            if (result != null)
            {
                QrzLookupOccurred = true;
                _statusValue.Text = "QRZ lookup complete.";
                PopulateFromCache();
            }
            else
            {
                _statusValue.Text = $"QRZ: {_manager.Qrz.LastError ?? "No data returned."}";
            }

            // Move focus to the status field so JAWS/NVDA announce the async result —
            // the label never regains focus on its own, so silently updating .Text
            // would otherwise go unnoticed by screen-reader users.
            _statusValue.Focus();
        }

        // Concise, screen-reader-friendly age phrase (cachedAtUtc is always UTC --
        // see QrzCacheEntry.CachedAtDt) so the status line says something a user can
        // actually judge trust from, instead of a content-free "cached" phrase.
        private static string FormatAge(DateTime cachedAtUtc)
        {
            var age = DateTime.UtcNow - cachedAtUtc;
            if (age.TotalMinutes < 1)  return "moments ago";
            if (age.TotalHours < 1)    return $"{(int)age.TotalMinutes} min ago";
            if (age.TotalHours < 24)   return $"{(int)age.TotalHours}h ago";
            if (age.TotalDays < 2)     return "yesterday";
            return $"{(int)age.TotalDays} days ago";
        }

        // QRZ returns the bare US operator-class letter (N/T/G/A/E) -- spell it out for
        // JAWS/NVDA users rather than making them parse a single letter. Anything else
        // (a foreign license class string, or a code not in this set) is shown as-is.
        private static readonly Dictionary<string, string> UsLicenseClassNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "N", "Novice" },
            { "T", "Technician" },
            { "G", "General" },
            { "A", "Advanced" },
            { "E", "Extra" },
        };

        private static string FormatLicenseClass(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "—";
            string name;
            return UsLicenseClassNames.TryGetValue(raw, out name) ? name : raw;
        }
    }
}
