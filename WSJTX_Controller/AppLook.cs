using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // The look of every window (handoff 31, 2026-10-12): one theme, one text size, one row
    // spacing and the operator's list colours, applied to every window the same way. Before
    // this, Options > Appearance reached only the five lists of the main window.
    //
    //   - Drawing only. No control's text, accessible name, role, tab order or focus is
    //     touched, so what a screen reader reads is unchanged.
    //   - A window joins with Attach(this) at the end of its constructor. Everything in it is
    //     then kept to the current look, including controls it adds later.
    //   - Text size is separate from Windows' own display scaling: Windows scales the window
    //     for the monitor, this makes the text (and the room it needs) larger on top of that.
    //     The window's own Font is never changed, so Windows' scaling is not triggered twice.
    //   - Fonts made here are kept and shared for the life of the program (a handful of
    //     sizes); no window owns or disposes them.
    //   - When Windows high contrast is on, Windows' colours are used everywhere and the
    //     theme, list colours and alert colours chosen here wait until it is off.
    internal enum LookTheme { Default = 0, Dark = 1, HighContrast = 2, System = 3 }

    internal sealed class LookPalette
    {
        public Color Surface, Text, Field, FieldText, Button, ButtonText, Border, Link;
    }

    internal static class AppLook
    {
        // ── the settings in force ───────────────────────────────────────────────
        public static LookTheme Theme { get; private set; } = LookTheme.Default;
        public static int TextPercent { get; private set; } = 100;       // 100, 125 or 150
        public static bool Comfortable { get; private set; }             // row spacing
        public static int ListFontSize { get; private set; } = 10;
        private static Color _listBack = SystemColors.Window, _listFore = SystemColors.WindowText, _listAlt = DefaultAlt;

        public static readonly int[] TextPercents = { 100, 125, 150 };
        public static readonly string[] TextPercentNames = { "Normal", "Large", "Larger" };
        public static readonly string[] ThemeNames = { "Default", "Dark", "High Contrast", "Follow Windows" };
        internal static readonly Color DefaultAlt = Color.FromArgb(233, 233, 233);

        // Raised after the look changed and every attached window was brought to it.
        public static event Action Changed;

        // A window that lays part of itself out in code does it again after the look changed
        // (its text size may have). Forgotten when the control is disposed.
        public static void AfterChange(Control owner, Action relayout)
        {
            if (owner == null || relayout == null) return;
            Changed += relayout;
            owner.Disposed += (s, e) => Changed -= relayout;
        }

        // Tests: pretend Windows high contrast / Windows dark mode are on or off. Null = ask Windows.
        internal static bool? TestWindowsHighContrast, TestWindowsDark;
        // Tests: windows are left exactly as they were built (to compare with and without).
        internal static bool TestOff;

        public static void Use(JimmySettings s)
        {
            if (s == null) return;
            Use((LookTheme)s.AppTheme, s.AppTextPercent, s.RowSpacingComfortable, s.ListFontSize, s.ListBackColor, s.ListForeColor, s.ListAltRowColor);
        }

        public static void Use(LookTheme theme, int textPercent, bool comfortable, int listFontSize, Color listBack, Color listFore, Color listAlt)
        {
            Theme = Enum.IsDefined(typeof(LookTheme), theme) ? theme : LookTheme.Default;
            TextPercent = TextPercents.Contains(textPercent) ? textPercent : 100;
            Comfortable = comfortable;
            ListFontSize = Math.Max(8, Math.Min(18, listFontSize));
            _listBack = listBack; _listFore = listFore; _listAlt = listAlt;
            ApplyAll();
        }

        internal static void ResetForTests()
        {
            TestWindowsHighContrast = null; TestWindowsDark = null;
            Use(LookTheme.Default, 100, false, 10, SystemColors.Window, SystemColors.WindowText, DefaultAlt);
        }

        public static float Scale => TextPercent / 100f;
        // A length in the code's own (100 percent) pixels, at the current text size.
        public static int Px(int at100) => (int)Math.Round(at100 * Scale);
        public static Point Px(Point at100) => new Point(Px(at100.X), Px(at100.Y));
        // The other way: a length or place on a window now, as pixels at 100 percent text.
        public static Point At100(Point now) => new Point((int)Math.Round(now.X / Scale), (int)Math.Round(now.Y / Scale));

        // ── theme ───────────────────────────────────────────────────────────────
        public static bool WindowsHighContrast => TestWindowsHighContrast ?? SystemInformation.HighContrast;

        private static bool WindowsDark()
        {
            if (TestWindowsDark.HasValue) return TestWindowsDark.Value;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }

        // The theme actually shown: Follow Windows resolved, and Default while Windows high
        // contrast is on.
        public static LookTheme EffectiveTheme =>
            WindowsHighContrast ? LookTheme.Default
            : Theme == LookTheme.System ? (WindowsDark() ? LookTheme.Dark : LookTheme.Default)
            : Theme;

        private static readonly LookPalette DarkPalette = new LookPalette
        {
            Surface = Color.FromArgb(32, 32, 32), Text = Color.FromArgb(232, 232, 232),
            Field = Color.FromArgb(48, 48, 48), FieldText = Color.FromArgb(240, 240, 240),
            Button = Color.FromArgb(62, 62, 62), ButtonText = Color.White,
            Border = Color.FromArgb(150, 150, 150), Link = Color.FromArgb(130, 180, 255),
        };
        private static readonly LookPalette HighContrastPalette = new LookPalette
        {
            Surface = Color.Black, Text = Color.White, Field = Color.Black, FieldText = Color.White,
            Button = Color.Black, ButtonText = Color.Yellow, Border = Color.White, Link = Color.Yellow,
        };

        // Null = Windows' own colours (Default theme, or Windows high contrast on).
        public static LookPalette Palette
        {
            get
            {
                switch (EffectiveTheme)
                {
                    case LookTheme.Dark: return DarkPalette;
                    case LookTheme.HighContrast: return HighContrastPalette;
                    default: return null;
                }
            }
        }

        // The list colours each theme comes with (Options' theme box sets these).
        public static void PresetListColors(LookTheme theme, out Color back, out Color fore, out Color alt)
        {
            switch (theme)
            {
                case LookTheme.Dark: back = Color.FromArgb(30, 30, 30); fore = Color.FromArgb(220, 220, 220); alt = Color.FromArgb(45, 45, 45); break;
                case LookTheme.HighContrast: back = Color.Black; fore = Color.Yellow; alt = Color.FromArgb(40, 40, 0); break;
                default: back = SystemColors.Window; fore = SystemColors.WindowText; alt = DefaultAlt; break;
            }
        }

        private static bool Same(Color a, Color b) => a.ToArgb() == b.ToArgb();

        // Whether these are one theme's own list colours rather than colours the operator picked.
        // The Default ones are also recognised as plain white and black, which is how they were
        // saved (as numbers) by every version before this one.
        public static bool IsPresetListColors(Color back, Color fore, Color alt, out LookTheme which)
        {
            foreach (var t in new[] { LookTheme.Default, LookTheme.Dark, LookTheme.HighContrast })
            {
                PresetListColors(t, out var b, out var f, out var a);
                if (Same(back, b) && Same(fore, f) && Same(alt, a)) { which = t; return true; }
            }
            if (Same(back, Color.White) && Same(fore, Color.Black) && Same(alt, DefaultAlt)) { which = LookTheme.Default; return true; }
            which = LookTheme.Default;
            return false;
        }

        // The colours lists are drawn in. Colours the operator picked are used as picked; a
        // theme's own colours follow the theme shown now (so Follow Windows changes them too).
        public static Color ListBack { get { ListColorsNow(out var b, out _, out _); return b; } }
        public static Color ListFore { get { ListColorsNow(out _, out var f, out _); return f; } }
        public static Color ListAlt { get { ListColorsNow(out _, out _, out var a); return a; } }

        private static void ListColorsNow(out Color back, out Color fore, out Color alt)
        {
            if (WindowsHighContrast) { back = SystemColors.Window; fore = SystemColors.WindowText; alt = SystemColors.Window; return; }
            if (IsPresetListColors(_listBack, _listFore, _listAlt, out _)) { PresetListColors(EffectiveTheme, out back, out fore, out alt); return; }
            back = _listBack; fore = _listFore; alt = _listAlt;
        }

        // An alert category's own row colours, as they may be drawn: none while Windows high
        // contrast is on; a text colour that cannot be read on its row is moved until it can.
        public static void AlertColors(Color? wantBack, Color? wantFore, Color rowBack, Color rowFore, out Color back, out Color fore)
        {
            if (WindowsHighContrast) { back = rowBack; fore = rowFore; return; }
            back = wantBack ?? rowBack;
            fore = wantFore ?? rowFore;
            if (wantBack.HasValue != wantFore.HasValue) fore = Readable(fore, back, 4.5);
        }

        // ── contrast (WCAG relative luminance) ──────────────────────────────────
        public static double Luminance(Color c)
        {
            double Lin(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        // The colour itself when it can be read on the background; else the same hue moved
        // toward white or black until it can.
        public static Color Readable(Color fore, Color back, double atLeast)
        {
            if (Contrast(fore, back) >= atLeast) return fore;
            Color toward = Luminance(back) < 0.18 ? Color.White : Color.Black;
            for (int step = 1; step <= 10; step++)
            {
                double t = step / 10.0;
                var c = Color.FromArgb((int)(fore.R + (toward.R - fore.R) * t), (int)(fore.G + (toward.G - fore.G) * t), (int)(fore.B + (toward.B - fore.B) * t));
                if (Contrast(c, back) >= atLeast) return c;
            }
            return toward;
        }

        // ── fonts and rows ──────────────────────────────────────────────────────
        private static readonly Dictionary<(string, int, FontStyle), Font> _fonts = new Dictionary<(string, int, FontStyle), Font>();

        // Shared, never disposed by a caller.
        public static Font FontOf(string family, float points, FontStyle style)
        {
            int quarter = Math.Max(4, (int)Math.Round(points * 4));
            var key = (family, quarter, style);
            if (!_fonts.TryGetValue(key, out var f)) _fonts[key] = f = new Font(family, quarter / 4f, style);
            return f;
        }

        // The station lists' own font: bold Consolas at the list size, larger with the text size.
        public static Font MainListFont => FontOf("Consolas", ListFontSize * Scale, FontStyle.Bold);

        // A list row's height, from the font it is drawn in.
        public static int RowHeight(Font font)
        {
            int h = TextRenderer.MeasureText("Ag", font).Height;
            return h + (Comfortable ? Math.Max(6, h * 2 / 5) : 2);
        }

        // ── windows ─────────────────────────────────────────────────────────────
        private sealed class State
        {
            public bool OwnFont, OwnBack, OwnFore, Themed, Hooked;
            public string Family; public float Points; public FontStyle Style;
            public Color Back, Fore;
            public float Applied = 1f;
            public float PlacedAt = 1f;      // the text size in force when a Placed control was made
            public FlatStyle Flat; public bool VisualBack;
            public ListRole Role; public bool OurPainter; public int ItemHeight;
            public bool RowImages, DisabledPaint;
        }
        internal enum ListRole { None, Plain, Main, MainOwnPainter }

        private static readonly ConditionalWeakTable<Control, State> _states = new ConditionalWeakTable<Control, State>();
        private static readonly List<WeakReference<Form>> _forms = new List<WeakReference<Form>>();
        private static bool _applying, _watchingWindows;

        // A window joins the shared look. Call at the end of the constructor.
        public static void Attach(Form f)
        {
            if (TestOff) return;
            if (f == null || _forms.Any(w => w.TryGetTarget(out var x) && ReferenceEquals(x, f))) return;
            _forms.RemoveAll(w => !w.TryGetTarget(out var x) || x.IsDisposed);
            _forms.Add(new WeakReference<Form>(f));
            f.Load += (s, e) => { Apply(f); FitToScreen(f); };
            f.HandleCreated += (s, e) => TitleBar(f);
            if (!_watchingWindows)
            {
                _watchingWindows = true;
                // Windows' own light/dark or high contrast setting changed.
                try { Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) => { if (e.Category == Microsoft.Win32.UserPreferenceCategory.General || e.Category == Microsoft.Win32.UserPreferenceCategory.Accessibility || e.Category == Microsoft.Win32.UserPreferenceCategory.Color) ApplyAllOnUiThread(); }; }
                catch { }
            }
            Apply(f);
        }

        // One of the station lists: bold Consolas at the list size. ownPainter: the window draws
        // the rows itself (the main window's lists, which colour rows by alert category).
        public static void MainList(ListBox lb, bool ownPainter = false)
        {
            if (lb == null) return;
            var st = _states.GetOrCreateValue(lb);
            st.Role = ownPainter ? ListRole.MainOwnPainter : ListRole.Main;
        }

        // A control a window made after it was built and placed from controls already on it
        // (below this one, as wide as that one): it is already at the current text size, so it
        // is not moved or sized again. Call before or just after adding it.
        public static void Placed(Control c)
        {
            if (c == null) return;
            var st = _states.GetOrCreateValue(c);
            st.Applied = Scale;
            st.PlacedAt = Scale;   // a font copied from a control already on the window is at this size too
            foreach (Control child in c.Controls) Placed(child);
        }

        private static void ApplyAllOnUiThread()
        {
            Form any = _forms.Select(w => w.TryGetTarget(out var x) ? x : null).FirstOrDefault(x => x != null && !x.IsDisposed && x.IsHandleCreated);
            if (any == null) return;
            try { any.BeginInvoke((Action)ApplyAll); } catch { }
        }

        private static void ApplyAll()
        {
            foreach (var w in _forms.ToArray())
                if (w.TryGetTarget(out var f) && !f.IsDisposed)
                {
                    Apply(f);
                    // Larger text may have made an open window larger than its screen.
                    if (f.IsHandleCreated && f.Visible) FitToScreen(f);
                }
            Changed?.Invoke();
        }

        public static void Apply(Form f)
        {
            if (f == null || f.IsDisposed || _applying) return;
            _applying = true;
            f.SuspendLayout();
            try
            {
                Capture(f, f);
                FixScale(f, Scale);
                var palette = Palette;
                Dress(f, f, palette);
                TitleBar(f);
            }
            finally
            {
                f.ResumeLayout(true);
                _applying = false;
            }
        }

        private static bool PlainSystem(Color c) =>
            c.A < 255 || (c.IsSystemColor && (c.ToKnownColor() == KnownColor.Control || c.ToKnownColor() == KnownColor.Window
                || c.ToKnownColor() == KnownColor.ControlText || c.ToKnownColor() == KnownColor.WindowText
                || c.ToKnownColor() == KnownColor.ControlLight || c.ToKnownColor() == KnownColor.ControlLightLight));

        private static bool Explicit(Control c, string property)
        {
            try { return TypeDescriptor.GetProperties(c)[property]?.ShouldSerializeValue(c) ?? false; }
            catch { return false; }
        }

        // First sight of a control: what it looked like before anything here touched it.
        private static void Capture(Control c, Form form)
        {
            var st = _states.GetOrCreateValue(c);
            if (!st.Hooked)
            {
                st.Hooked = true;
                st.OwnFont = Explicit(c, "Font");
                var font = c.Font;
                st.Family = font.FontFamily.Name; st.Points = font.SizeInPoints / (st.OwnFont ? st.PlacedAt : 1f); st.Style = font.Style;
                st.Back = c.BackColor; st.Fore = c.ForeColor;
                // A colour of its own means one chosen for a reason (a red warning, a coloured
                // status). Windows' plain window colours written out by the form designer are not that.
                st.OwnBack = Explicit(c, "BackColor") && !PlainSystem(st.Back);
                st.OwnFore = Explicit(c, "ForeColor") && !PlainSystem(st.Fore);
                if (c is ButtonBase bb) { st.Flat = bb.FlatStyle; st.VisualBack = bb.UseVisualStyleBackColor; }
                if (c is ComboBox cb) st.Flat = cb.FlatStyle;
                // A number box is a small container of its own and would also resize itself when
                // its font changes -- on top of the sizing done here (seen: the box moved from the
                // main window into Options landed in the wrong place at larger text).
                if (c is UpDownBase ud) ud.AutoScaleMode = AutoScaleMode.None;
                if (c is ListBox lb && !(c is CheckedListBox))
                {
                    st.ItemHeight = lb.ItemHeight;
                    if (st.Role == ListRole.None && lb.DrawMode == DrawMode.Normal) st.Role = ListRole.Plain;
                }
                // A control added after the window was built is brought to the look too. Its
                // place and size were given in the code's own pixels, so its scale starts at 1.
                c.ControlAdded += (s, e) => Later(c.FindForm());
                if (c is ListBox || c is TextBoxBase || c is ListView || c is TreeView) c.HandleCreated += (s, e) => Scrollbars(c);
            }
            foreach (Control child in c.Controls) Capture(child, form);
        }

        private static readonly HashSet<Form> _later = new HashSet<Form>();
        private static void Later(Form f)
        {
            if (_applying || f == null || f.IsDisposed || !f.IsHandleCreated || !_later.Add(f)) return;
            try { f.BeginInvoke((Action)(() => { _later.Remove(f); Apply(f); })); }
            catch { _later.Remove(f); }
        }

        // Layout: every control is at the same scale as the text. A control's own Scale moves
        // and sizes it and everything inside it, so only the outermost stale one is scaled.
        private static void FixScale(Control c, float target)
        {
            var st = _states.GetOrCreateValue(c);
            if (Math.Abs(st.Applied - target) > 0.001f)
            {
                float ratio = target / st.Applied;
                c.Scale(new SizeF(ratio, ratio));
                Scaled(c, ratio, true);
            }
            foreach (Control child in c.Controls) FixScale(child, target);
        }

        private static void Scaled(Control c, float ratio, bool top)
        {
            var st = _states.GetOrCreateValue(c);
            st.Applied *= ratio;
            if (c is ListView lv)
                foreach (ColumnHeader col in lv.Columns)
                    if (col.Width > 0) col.Width = (int)Math.Round(col.Width * ratio);
            foreach (Control child in c.Controls) Scaled(child, ratio, false);
        }

        private static void BaseFont(Control c, out string family, out float points, out FontStyle style)
        {
            for (Control at = c; at != null; at = at.Parent)
            {
                if (_states.TryGetValue(at, out var st) && st.Hooked && (st.OwnFont || at.Parent == null))
                {
                    family = st.Family; points = st.Points; style = st.Style;
                    return;
                }
            }
            _states.TryGetValue(c, out var own);
            family = own?.Family ?? "Microsoft Sans Serif"; points = own?.Points ?? 8.25f; style = own?.Style ?? FontStyle.Regular;
        }

        private static bool IsList(Control c) => c is ListBox || c is ListView || c is TreeView;

        private static Color BackBehind(Control c)
        {
            for (Control at = c; at != null; at = at.Parent)
                if (at.BackColor.A == 255) return at.BackColor;
            return SystemColors.Control;
        }

        private static void Dress(Control c, Form form, LookPalette p)
        {
            var st = _states.GetOrCreateValue(c);

            // Font. The window's own Font is left alone (see the top of this file).
            if (!(c is Form))
            {
                Font want;
                if (st.Role == ListRole.Main || st.Role == ListRole.MainOwnPainter) want = MainListFont;
                else
                {
                    BaseFont(c, out string family, out float points, out FontStyle style);
                    want = FontOf(family, points * Scale * (IsList(c) ? ListFontSize / 10f : 1f), style);
                }
                if (!c.Font.Equals(want)) c.Font = want;
            }

            // Lists: the list colours and row height, whatever the theme.
            if (c is ListBox lb && !(c is CheckedListBox) && st.Role != ListRole.None)
            {
                if (st.Role != ListRole.MainOwnPainter && !st.OurPainter)
                {
                    st.OurPainter = true;
                    lb.DrawMode = DrawMode.OwnerDrawFixed;
                    lb.DrawItem += DrawRow;
                }
                int h = RowHeight(lb.Font);
                if (lb.ItemHeight != h) lb.ItemHeight = h;
                if (!Same(lb.BackColor, ListBack)) lb.BackColor = ListBack;
                if (!Same(lb.ForeColor, ListFore)) lb.ForeColor = ListFore;
                lb.Invalidate();
            }
            else if (c is CheckedListBox || c is ListView || c is TreeView)
            {
                if (!Same(c.BackColor, ListBack)) c.BackColor = ListBack;
                if (!Same(c.ForeColor, ListFore)) c.ForeColor = ListFore;
                if (c is ListView lv) RowImages(lv, st);
            }
            else Colors(c, st, p);

            if (c.IsHandleCreated && (c is ListBox || c is TextBoxBase || c is ListView || c is TreeView)) Scrollbars(c);
            foreach (Control child in c.Controls) Dress(child, form, p);
        }

        // A details list's rows cannot be made taller directly; an empty picture of the wanted
        // height in front of each row does it. Only where the list has no pictures of its own.
        private static void RowImages(ListView lv, State st)
        {
            if (lv.View != View.Details || (lv.SmallImageList != null && !st.RowImages)) return;
            int h = Comfortable ? RowHeight(lv.Font) : 0;
            if (h == 0)
            {
                if (st.RowImages) { var old = lv.SmallImageList; lv.SmallImageList = null; old?.Dispose(); st.RowImages = false; }
                return;
            }
            if (st.RowImages && lv.SmallImageList != null && lv.SmallImageList.ImageSize.Height == h) return;
            var prev = st.RowImages ? lv.SmallImageList : null;
            lv.SmallImageList = new ImageList { ImageSize = new Size(1, Math.Min(256, h)) };
            st.RowImages = true;
            prev?.Dispose();
        }

        private static void Colors(Control c, State st, LookPalette p)
        {
            if (p == null)
            {
                if (!st.Themed) return;
                st.Themed = false;
                c.BackColor = st.OwnBack ? st.Back : Color.Empty;
                c.ForeColor = st.OwnFore ? st.Fore : Color.Empty;
                if (c is ButtonBase b0) { b0.FlatStyle = st.Flat; if (st.VisualBack) b0.UseVisualStyleBackColor = true; }
                if (c is ComboBox c0) c0.FlatStyle = st.Flat;
                if (c is LinkLabel l0) l0.LinkColor = Color.Empty;
                return;
            }
            st.Themed = true;
            if (c is Form || c is TabPage)
            {
                c.BackColor = st.OwnBack && !(c is TabPage) ? st.Back : p.Surface;
                c.ForeColor = Readable(st.OwnFore ? st.Fore : p.Text, c.BackColor, 4.5);
            }
            else if (c is TextBoxBase || c is ComboBox || c is NumericUpDown || c is DateTimePicker)
            {
                c.BackColor = p.Field;
                c.ForeColor = p.FieldText;
                if (c is ComboBox cb) cb.FlatStyle = FlatStyle.Flat;
            }
            else if (c is Button btn)
            {
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderColor = p.Border;
                btn.BackColor = p.Button;
                btn.ForeColor = p.ButtonText;
                if (!st.DisabledPaint) { st.DisabledPaint = true; btn.Paint += PaintDisabledButton; btn.EnabledChanged += (s, e) => btn.Invalidate(); }
            }
            else
            {
                // Labels, check boxes, groups, panels: the colours of what they sit on, unless
                // the window gave them a colour of its own -- which is kept, and kept readable.
                c.BackColor = st.OwnBack ? st.Back : Color.Empty;
                Color behind = BackBehind(c);
                c.ForeColor = st.OwnFore ? Readable(st.Fore, behind, 4.5)
                    : st.OwnBack ? Readable(p.Text, behind, 4.5) : Color.Empty;
                if (c is LinkLabel link) link.LinkColor = p.Link;
            }
        }

        // A switched-off button under a theme: Windows draws its text in a darker shade of the
        // button itself, which on a dark button cannot be seen at all. Its text is drawn again,
        // dimmer than a working button's but readable (at least 3 to 1).
        private static void PaintDisabledButton(object sender, PaintEventArgs e)
        {
            var btn = (Button)sender;
            var p = Palette;
            if (btn.Enabled || p == null || btn.Text.Length == 0) return;
            var inside = Rectangle.Inflate(btn.ClientRectangle, -3, -3);
            using (var b = new SolidBrush(btn.BackColor)) e.Graphics.FillRectangle(b, inside);
            Color dim = Color.FromArgb((p.ButtonText.R + btn.BackColor.R) / 2, (p.ButtonText.G + btn.BackColor.G) / 2, (p.ButtonText.B + btn.BackColor.B) / 2);
            TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font, inside, Readable(dim, btn.BackColor, 3.0),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.HidePrefix);
        }

        // Rows of a plain list: the list colours, every other row shaded, the selected row in
        // Windows' own selection colours, and the focus rectangle.
        private static void DrawRow(object sender, DrawItemEventArgs e)
        {
            var lb = (ListBox)sender;
            if (e.Index < 0 || e.Index >= lb.Items.Count) { using (var b = new SolidBrush(lb.BackColor)) e.Graphics.FillRectangle(b, e.Bounds); return; }
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = selected ? SystemColors.Highlight : e.Index % 2 == 1 ? ListAlt : lb.BackColor;
            Color fore = selected ? SystemColors.HighlightText : lb.ForeColor;
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            string text = lb.GetItemText(lb.Items[e.Index]);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, text, lb.Font, e.Bounds, fore, flags);
            // A list that scrolls sideways when rows are too long keeps doing so: an owner-drawn
            // list has to be told how wide its rows are.
            if (lb.HorizontalScrollbar)
            {
                int w = TextRenderer.MeasureText(e.Graphics, text, lb.Font, Size.Empty, flags).Width + 4;
                if (w > lb.HorizontalExtent) lb.HorizontalExtent = w;
            }
            e.DrawFocusRectangle();
        }

        // ── the window frame ────────────────────────────────────────────────────
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        private static bool DarkNow { get { var p = Palette; return p != null && Luminance(p.Surface) < 0.2; } }

        private static void TitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            try { int on = DarkNow ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)); } catch { }
        }

        private static void Scrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, DarkNow ? "DarkMode_Explorer" : null, null); } catch { }
        }

        // A window that came out larger than the screen it is on (larger text on a small
        // screen): it is made to fit, and scrolls, so every control stays reachable.
        internal static void FitToScreen(Form f)
        {
            if (f.WindowState != FormWindowState.Normal) return;
            Rectangle area = Screen.FromControl(f).WorkingArea;
            if (f.Width <= area.Width && f.Height <= area.Height) return;
            Size content = f.ClientSize;
            if (f.MinimumSize.Width > area.Width || f.MinimumSize.Height > area.Height)
                f.MinimumSize = new Size(Math.Min(f.MinimumSize.Width, area.Width), Math.Min(f.MinimumSize.Height, area.Height));
            if (!f.AutoScroll) { f.AutoScroll = true; f.AutoScrollMinSize = Size.Empty; }
            f.Size = new Size(Math.Min(f.Width, area.Width), Math.Min(f.Height, area.Height));
            f.Location = new Point(Math.Max(area.Left, Math.Min(f.Left, area.Right - f.Width)), Math.Max(area.Top, Math.Min(f.Top, area.Bottom - f.Height)));
        }

        // ── remembered window places ────────────────────────────────────────────
        // Where the larger windows were and how big, per computer (never part of an exported
        // customization). The main window keeps its own, as it always has.
        public static readonly string[] BoundsKeys = { "Options", "Logbook", "Spots", "SmartMode", "Contesting", "NotificationHistory" };
        private static readonly Dictionary<string, string> _bounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static int _boundsGeneration;

        public static void LoadBounds(Func<string, string> read)
        {
            _bounds.Clear();
            foreach (var key in BoundsKeys)
            {
                string v = read?.Invoke("window_" + key);
                if (!string.IsNullOrWhiteSpace(v)) _bounds[key] = v;
            }
        }

        public static void SaveBounds(Action<string, string> write)
        {
            foreach (var key in BoundsKeys) write?.Invoke("window_" + key, _bounds.TryGetValue(key, out var v) ? v : "");
        }

        // Options > Appearance > Reset Window Positions. Windows open now do not put theirs back.
        public static void ForgetBounds() { _bounds.Clear(); _boundsGeneration++; }

        internal static string FormatBounds(Rectangle r, bool maximized, int textPercent) =>
            $"{r.X},{r.Y},{r.Width},{r.Height},{(maximized ? 1 : 0)},{textPercent}";

        internal static bool TryParseBounds(string text, out Rectangle r, out bool maximized, out int textPercent)
        {
            r = Rectangle.Empty; maximized = false; textPercent = 100;
            var parts = (text ?? "").Split(',');
            if (parts.Length < 6) return false;
            var n = new int[6];
            for (int i = 0; i < 6; i++)
                if (!int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return false;
            if (n[2] < 50 || n[3] < 50 || n[2] > 20000 || n[3] > 20000 || !TextPercents.Contains(n[5])) return false;
            r = new Rectangle(n[0], n[1], n[2], n[3]); maximized = n[4] == 1; textPercent = n[5];
            return true;
        }

        // The window opens where it was last closed, at the size it had (adjusted if the text
        // size has changed since), brought back onto a screen that exists now. Call after Attach.
        public static void RememberBounds(Form f, string key)
        {
            if (f == null || TestOff) return;
            int generation = _boundsGeneration;
            f.Load += (s, e) =>
            {
                if (!_bounds.TryGetValue(key, out var saved) || !TryParseBounds(saved, out var r, out bool max, out int percent)) return;
                bool sizable = f.FormBorderStyle == FormBorderStyle.Sizable || f.FormBorderStyle == FormBorderStyle.SizableToolWindow;
                Size size = sizable ? new Size(r.Width * TextPercent / percent, r.Height * TextPercent / percent) : f.Size;
                var areas = Screen.AllScreens.Select(sc => sc.WorkingArea).ToList();
                var placed = OnScreen(new Rectangle(r.Location, size), f.MinimumSize, areas, Screen.PrimaryScreen.WorkingArea);
                f.StartPosition = FormStartPosition.Manual;
                if (sizable) f.Bounds = placed; else f.Location = placed.Location;
                if (max && sizable && f.MaximizeBox) f.WindowState = FormWindowState.Maximized;
            };
            f.FormClosing += (s, e) =>
            {
                if (e.Cancel || generation != _boundsGeneration || f.WindowState == FormWindowState.Minimized) return;
                Rectangle now = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
                _bounds[key] = FormatBounds(now, f.WindowState == FormWindowState.Maximized, TextPercent);
            };
        }

        // A saved window rectangle, made to fit the screens there are now: on a screen that is
        // gone it comes back to the main screen; too large, it is brought down to the screen.
        public static Rectangle OnScreen(Rectangle saved, Size minimum, IEnumerable<Rectangle> workingAreas, Rectangle primaryArea)
        {
            var center = new Point(saved.X + saved.Width / 2, saved.Y + saved.Height / 2);
            Rectangle area = primaryArea;
            bool found = false;
            foreach (var a in workingAreas ?? Enumerable.Empty<Rectangle>())
                if (a.Contains(center)) { area = a; found = true; break; }
            int w = Math.Min(Math.Max(saved.Width, minimum.Width), area.Width);
            int h = Math.Min(Math.Max(saved.Height, minimum.Height), area.Height);
            int x = found ? saved.X : area.Left + (area.Width - w) / 2;
            int y = found ? saved.Y : area.Top + (area.Height - h) / 2;
            x = Math.Max(area.Left, Math.Min(x, area.Right - w));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - h));
            return new Rectangle(x, y, w, h);
        }
    }
}
