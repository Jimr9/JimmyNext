using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Alert regions (2026-09-29, after Nexus 1.15's "Alerts can now be kept to the parts of the
    // world you actually chase"): continents and DXCC countries the new-station, new-grid and CQ
    // alert SOUNDS are kept to. Empty = All regions, the default, so nothing changes until chosen.
    // Calling me, always wanted and wanted anywhere always sound; awards, Still Need, the station
    // lists, ranking and automatic calling never read this. Stored per profile as
    // "alertRegions=EU,AS;291,339" (continents;DXCC numbers).
    internal sealed class AlertRegions
    {
        internal static readonly (string Code, string Name)[] Continents =
        {
            ("AF", "Africa"), ("AN", "Antarctica"), ("AS", "Asia"), ("EU", "Europe"),
            ("NA", "North America"), ("OC", "Oceania"), ("SA", "South America"),
        };

        internal readonly HashSet<string> ContinentCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<int> Dxcc = new HashSet<int>();

        internal bool IsAll => ContinentCodes.Count == 0 && Dxcc.Count == 0;

        internal static AlertRegions Parse(string text)
        {
            var r = new AlertRegions();
            string[] halves = (text ?? "").Split(';');
            foreach (string c in halves[0].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (Continents.Any(x => x.Code.Equals(c.Trim(), StringComparison.OrdinalIgnoreCase))) r.ContinentCodes.Add(c.Trim().ToUpperInvariant());
            if (halves.Length > 1)
                foreach (string d in halves[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(d.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0) r.Dxcc.Add(n);
            return r;
        }

        internal string Format() => IsAll ? "" :
            string.Join(",", ContinentCodes.OrderBy(c => c)) + ";" + string.Join(",", Dxcc.OrderBy(d => d));

        // A station whose continent and country are both unknown still sounds: a missing lookup
        // must never silence an alert.
        internal bool Allows(string continent, int dxcc)
        {
            if (IsAll) return true;
            bool continentKnown = !string.IsNullOrWhiteSpace(continent);
            if (!continentKnown && dxcc <= 0) return true;
            return (continentKnown && ContinentCodes.Contains(continent.Trim())) || (dxcc > 0 && Dxcc.Contains(dxcc));
        }

        internal string Describe(Func<int, string> countryName)
        {
            if (IsAll) return "All regions";
            var parts = Continents.Where(c => ContinentCodes.Contains(c.Code)).Select(c => c.Name)
                .Concat(Dxcc.Select(d => countryName?.Invoke(d) ?? ("DXCC " + d)).OrderBy(n => n));
            return string.Join(", ", parts);
        }
    }

    // Options > Sounds > Alert regions: All regions, or chosen continents and countries.
    // Keyboard: Tab through All regions, the continent boxes, the country filter, the country
    // list (Space checks), OK and Cancel. Esc cancels, Enter in the list does not close.
    internal sealed class AlertRegionsDlg : Form
    {
        private readonly RadioButton _all, _chosen;
        private readonly List<CheckBox> _continentBoxes = new List<CheckBox>();
        private readonly TextBox _filter;
        private readonly CheckedListBox _countries;
        private readonly List<ClubLogEntity> _entities;
        private readonly HashSet<int> _chosenDxcc;
        private bool _filling;

        internal AlertRegions Result { get; private set; }

        internal AlertRegionsDlg(AlertRegions current, IEnumerable<ClubLogEntity> entities, System.Drawing.Font font)
        {
            Text = "Alert regions";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(460, 470);
            Font = font;
            _entities = (entities ?? Enumerable.Empty<ClubLogEntity>()).Where(e => !e.Deleted && e.Adif > 0)
                .GroupBy(e => e.Adif).Select(g => g.First()).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _chosenDxcc = new HashSet<int>(current.Dxcc);
            int tab = 0;

            var note = new Label
            {
                Text = "New station, new grid and CQ alert sounds only for these places. Calling me and wanted calls always sound.",
                Location = new System.Drawing.Point(10, 8), Size = new System.Drawing.Size(440, 32),
            };
            Controls.Add(note);
            _all = new RadioButton { Text = "&All regions", AccessibleName = "All regions", Location = new System.Drawing.Point(10, 44), AutoSize = true, TabIndex = tab++, Checked = current.IsAll };
            _chosen = new RadioButton { Text = "&Only these regions", AccessibleName = "Only these regions", Location = new System.Drawing.Point(10, 66), AutoSize = true, TabIndex = tab++, Checked = !current.IsAll };
            Controls.Add(_all);
            Controls.Add(_chosen);

            var contGroup = new GroupBox { Text = "Continents", Location = new System.Drawing.Point(10, 92), Size = new System.Drawing.Size(440, 96), TabIndex = tab++ };
            for (int i = 0; i < AlertRegions.Continents.Length; i++)
            {
                var (code, name) = AlertRegions.Continents[i];
                var cb = new CheckBox
                {
                    Text = name, AccessibleName = name, Tag = code, AutoSize = true, TabIndex = i,
                    Location = new System.Drawing.Point(10 + (i % 3) * 140, 20 + (i / 3) * 24),
                    Checked = current.ContinentCodes.Contains(code),
                };
                cb.CheckedChanged += (s, e) => { if (!_filling && cb.Checked) _chosen.Checked = true; };
                _continentBoxes.Add(cb);
                contGroup.Controls.Add(cb);
            }
            Controls.Add(contGroup);

            Controls.Add(new Label { Text = "Find &country:", Location = new System.Drawing.Point(10, 200), AutoSize = true });
            _filter = new TextBox { AccessibleName = "Find country", Location = new System.Drawing.Point(110, 197), Size = new System.Drawing.Size(340, 20), TabIndex = tab++ };
            Controls.Add(_filter);
            _countries = new CheckedListBox
            {
                AccessibleName = "Countries", Location = new System.Drawing.Point(10, 224), Size = new System.Drawing.Size(440, 200),
                CheckOnClick = true, TabIndex = tab++, IntegralHeight = false,
            };
            Controls.Add(_countries);
            if (_entities.Count == 0)
                _countries.Items.Add("(Country list not downloaded yet)");

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new System.Drawing.Point(290, 436), Size = new System.Drawing.Size(75, 26), TabIndex = tab++ };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new System.Drawing.Point(375, 436), Size = new System.Drawing.Size(75, 26), TabIndex = tab++ };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            _filter.TextChanged += (s, e) => FillCountries();
            _countries.ItemCheck += (s, e) =>
            {
                if (_filling || !(_countries.Items[e.Index] is CountryItem item)) return;
                if (e.NewValue == CheckState.Checked) { _chosenDxcc.Add(item.Adif); _chosen.Checked = true; }
                else _chosenDxcc.Remove(item.Adif);
            };
            FillCountries();
            FormClosing += (s, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                var r = new AlertRegions();
                if (_chosen.Checked)
                {
                    foreach (var cb in _continentBoxes) if (cb.Checked) r.ContinentCodes.Add((string)cb.Tag);
                    foreach (int d in _chosenDxcc) r.Dxcc.Add(d);
                }
                Result = r; // "Only these regions" with nothing chosen is All regions
            };
        }

        private sealed class CountryItem
        {
            public int Adif; public string Name;
            public override string ToString() => Name;
        }

        private void FillCountries()
        {
            if (_entities.Count == 0) return;
            string f = _filter.Text.Trim();
            _filling = true;
            _countries.BeginUpdate();
            _countries.Items.Clear();
            foreach (var e in _entities)
            {
                if (f.Length > 0 && e.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _countries.Items.Add(new CountryItem { Adif = e.Adif, Name = e.Name }, _chosenDxcc.Contains(e.Adif));
            }
            _countries.EndUpdate();
            _filling = false;
        }
    }
}
