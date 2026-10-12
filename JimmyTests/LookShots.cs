using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using WSJTX_Controller;

// Visual check of the shared look (handoff 31): builds each window at one theme, text size and
// row spacing, saves a picture of it, and lists text that does not fit and controls that fall
// outside their window. Nothing is shown on screen and no window takes the keyboard focus.
// Run it from a copy of the test folder in which System.IO.Ports.dll is the Windows one
// (the one under runtimes/win in the built program's folder); the test folder's own
// is a placeholder that cannot list ports, and Options needs to.
//   JimmyTests.exe --look-shots <folder> <Default|Dark|HighContrast> <100|125|150> <compact|comfortable> [name filter]
static class LookShots
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static int Run(string[] args)
    {
        string dir = args[1];
        var theme = (LookTheme)Enum.Parse(typeof(LookTheme), args[2], true);
        int percent = int.Parse(args[3]);
        bool comfortable = args.Length > 4 && args[4].StartsWith("comf", StringComparison.OrdinalIgnoreCase);
        string filter = args.Length > 5 ? args[5] : null;
        int listSize = args.Length > 6 ? int.Parse(args[6]) : 10;
        Directory.CreateDirectory(dir);
        int problems = 0;
        var t = new Thread(() =>
        {
            AppLook.TestOff = Environment.GetEnvironmentVariable("JIMMY_LOOK_OFF") == "1";
            AppLook.PresetListColors(theme, out var back, out var fore, out var alt);
            AppLook.Use(theme, percent, comfortable, listSize, back, fore, alt);
            foreach (var (name, make) in Windows())
            {
                if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    using (var f = make())
                    {
                        if (f == null) continue;
                        Bring(f);
                        // The main window sets the look from its own settings file when it loads;
                        // put the look under test back (this is also the live change Options makes).
                        // (JIMMY_LOOK_ASSAVED=1: leave it as the settings file has it, to check startup.)
                        if (Environment.GetEnvironmentVariable("JIMMY_LOOK_ASSAVED") != "1")
                            AppLook.Use(theme, percent, comfortable, listSize, back, fore, alt);
                        Application.DoEvents();
                        var pages = f.GetType().GetField("_categoryListBox", Any)?.GetValue(f) as ListBox;
                        if (pages == null || pages.Items.Count == 0) problems += Shoot(f, dir, name);
                        else
                            for (int i = 0; i < pages.Items.Count; i++)
                            {
                                pages.SelectedIndex = i;
                                Application.DoEvents();
                                problems += Shoot(f, dir, $"{name}-{i + 1:00}-{Safe(pages.GetItemText(pages.Items[i]))}");
                            }
                        foreach (TabControl tabs in All(f).OfType<TabControl>().ToList())
                            for (int i = 1; i < tabs.TabPages.Count; i++)
                            {
                                tabs.SelectedIndex = i;
                                Application.DoEvents();
                                problems += Shoot(f, dir, $"{name}-tab{i + 1}");
                            }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR {name}: {(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message}");
                    if (Environment.GetEnvironmentVariable("JIMMY_LOOK_TRACE") == "1") Console.WriteLine((ex.InnerException ?? ex).StackTrace);
                    problems++;
                }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(240000)) { Console.WriteLine("  ERROR: timed out"); return 1; }
        Console.WriteLine($"=== look shots {theme} {percent}% {(comfortable ? "comfortable" : "compact")}: {problems} thing(s) to look at ===");
        return 0;
    }

    static string Safe(string s) => new string((s ?? "").Where(ch => char.IsLetterOrDigit(ch)).ToArray());

    // The window as it is once loaded, without putting it on the screen.
    static readonly HashSet<Form> _brought = new HashSet<Form>();
    static void Bring(Form f)
    {
        if (!_brought.Add(f)) return;
        f.StartPosition = FormStartPosition.Manual;
        var _ = f.Handle;
        typeof(Control).GetMethod("CreateControl", Any, null, new[] { typeof(bool) }, null).Invoke(f, new object[] { true });
        typeof(Form).GetMethod("OnLoad", Any).Invoke(f, new object[] { EventArgs.Empty });
        for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
        typeof(Control).GetMethod("CreateControl", Any, null, new[] { typeof(bool) }, null).Invoke(f, new object[] { true });
        f.PerformLayout();
        Application.DoEvents();
    }

    static IEnumerable<Control> All(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (var d in All(child)) yield return d;
        }
    }

    // Visible by its own setting (Control.Visible is false for everything in a window that is
    // not on the screen).
    static readonly MethodInfo GetState = typeof(Control).GetMethod("GetState", Any);
    static bool OwnVisible(Control c)
    {
        try { return (bool)GetState.Invoke(c, new object[] { Enum.ToObject(GetState.GetParameters()[0].ParameterType, 2) }); }
        catch { return true; }
    }
    static bool Shown(Control c, Form f)
    {
        for (Control at = c; at != null && at != f; at = at.Parent)
        {
            if (!OwnVisible(at)) return false;
            if (at is TabPage tp && tp.Parent is TabControl tc && tc.SelectedTab != tp) return false;
        }
        return true;
    }

    static int Shoot(Form f, string dir, string name)
    {
        int problems = 0;
        typeof(Control).GetMethod("CreateControl", Any, null, new[] { typeof(bool) }, null).Invoke(f, new object[] { true });
        f.PerformLayout();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(10); }
        using (var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        var notes = new List<string>();
        foreach (Control c in All(f))
        {
            if (!Shown(c, f) || c.Width <= 0 || c.Height <= 0) continue;
            string id = $"{c.GetType().Name} '{Short(c.Text.Length > 0 ? c.Text : c.AccessibleName ?? c.Name)}'";
            // Text that does not fit its control.
            if ((c is Button || c is CheckBox || c is RadioButton || c is Label) && !c.AutoSize && c.Text.Length > 0)
            {
                int extra = c is Button ? 8 : c is Label ? 0 : 20;
                Size one = TextRenderer.MeasureText(c.Text, c.Font, Size.Empty, c is Label ? TextFormatFlags.NoPrefix : TextFormatFlags.Default);
                if (one.Width + extra > c.Width + 2)
                {
                    Size wrapped = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(1, c.Width - extra), 0), TextFormatFlags.WordBreak);
                    bool wraps = !(c is Button b && false);
                    if (!wraps || wrapped.Height > c.Height + 2 || wrapped.Width + extra > c.Width + 2)
                        notes.Add($"text does not fit: {id} needs {one.Width + extra}x{one.Height} (wrapped {wrapped.Width}x{wrapped.Height}), has {c.Width}x{c.Height}");
                }
                else if (one.Height > c.Height + 3 && !(c is Label))
                    notes.Add($"text too tall: {id} needs {one.Height}, has {c.Height}");
            }
            // A control that falls outside what holds it, where that does not scroll.
            var parent = c.Parent;
            if (parent != null && !(parent is ScrollableControl sc && sc.AutoScroll) && !(parent is TabControl) && c.Dock == DockStyle.None)
            {
                var room = parent.ClientRectangle;
                if (c.Right > room.Right + 4 || c.Bottom > room.Bottom + 4 || c.Left < -4 || c.Top < -4)
                    notes.Add($"outside its {parent.GetType().Name} ({room.Width}x{room.Height}): {id} at {c.Left},{c.Top} size {c.Width}x{c.Height}");
            }
            // Two controls on top of each other.
            if (parent != null && c.Dock == DockStyle.None && !(c is Panel) && !(c is GroupBox))
                foreach (Control other in parent.Controls)
                {
                    if (ReferenceEquals(other, c) || other.Dock != DockStyle.None || other is Panel || other is GroupBox || !Shown(other, f)) continue;
                    if (parent.Controls.GetChildIndex(other) < parent.Controls.GetChildIndex(c)) continue;
                    var cut = Rectangle.Intersect(c.Bounds, other.Bounds);
                    if (cut.Width > 3 && cut.Height > 3)
                        notes.Add($"overlap {cut.Width}x{cut.Height}: {id} and {other.GetType().Name} '{Short(other.Text.Length > 0 ? other.Text : other.AccessibleName ?? other.Name)}'");
                }
        }
        if (Environment.GetEnvironmentVariable("JIMMY_LOOK_FONTS") == "1")
            foreach (Control c in All(f))
                if (Shown(c, f) && (c is Button || c is Label || c is ListBox || c is NumericUpDown || c is ComboBox))
                    Console.WriteLine($"      font {c.Font.Name} {c.Font.SizeInPoints:0.##} {c.Font.Style}: {c.GetType().Name} '{Short(c.Text.Length > 0 ? c.Text : c.AccessibleName ?? c.Name)}' {c.Bounds}");
        var area = Screen.PrimaryScreen.WorkingArea;
        Console.WriteLine($"  {name}: {f.Width}x{f.Height}{(f.Width > area.Width || f.Height > area.Height ? "  LARGER THAN SCREEN " + area.Width + "x" + area.Height : "")}{(f.AutoScroll ? " (scrolls)" : "")}");
        foreach (var n in notes.Distinct()) { Console.WriteLine("      " + n); problems++; }
        return problems;
    }

    static string Short(string s) => s == null ? "" : s.Replace("\r", " ").Replace("\n", " ").Substring(0, Math.Min(40, s.Replace("\r", " ").Replace("\n", " ").Length));

    static Controller Ctrl()
    {
        var ctrl = new Controller();
        ctrl.callCqOptionsButton = new Button { Visible = false };
        ctrl.ignoreWeakSnrCheckBox = new CheckBox();
        ctrl.minSnrNumUpDown = new NumericUpDown { Minimum = -30, Maximum = 20, Value = -24 };
        ctrl.removeOnWeakSnrCheckBox = new CheckBox();
        ctrl.hotkeyConfig = new HotkeyConfig();
        return ctrl;
    }

    static IEnumerable<(string, Func<Form>)> Windows()
    {
        yield return ("Main", () => new Controller());
        // The main window with the plain two-list layout (the settings copy may have the advanced one).
        yield return ("MainSimple", () =>
        {
            var c = new Controller();
            Bring(c);
            c.Settings.AdvancedCallLayout = false;
            foreach (var name in new[] { "advancedCallLayout" })
            {
                var fld = typeof(Controller).GetField(name, Any);
                var prop = typeof(Controller).GetProperty(name, Any);
                if (fld != null) fld.SetValue(c, false); else prop?.SetValue(c, false);
            }
            c.ApplyAdvancedLayout();
            return c;
        });
        // Options takes some of its controls from the loaded main window.
        yield return ("Options", () =>
        {
            var c = new Controller();
            Bring(c);
            var wc = typeof(Controller).GetField("wsjtxClient", Any)?.GetValue(c) as WsjtxClient ?? new WsjtxClient(c, 2237, false, false, WsjtxClient.TxModes.LISTEN);
            return new OptionsDlg(wc, c);
        });
        yield return ("Spots", () => new OtaSpotsWindow(null, null, null));
        yield return ("Logbook", () => new LogbookWindow(null, () => "FAKE", () => "FAKE", () => "FAKE", () => "FAKE", () => "FAKE", () => "FAKE", () => "FAKE", () => "FAKE"));
        yield return ("Contesting", () => new ContestingWindow(() => "K5KPE", () => "EM48", () => "K5KPE", () => null, () => new StationSettings()));
        yield return ("SmartMode", () => new SmartModeWindow(Ctrl()));
        yield return ("NotificationHistory", () => new NotificationHistoryWindow(new NotificationHistory(), () => true, _ => { }));
        yield return ("Help", () => new HelpDlg(Ctrl(), "Jimmy Next", "Help text line one.\r\nLine two of the help text, which is a little longer than the first."));
        yield return ("ManualCall", () => new ManualCallDlg("K1ABC"));
        yield return ("SetTxFreq", () => new SetTxFreqDlg(1500, 200, 2900, 50));
        yield return ("TextPrompt", () => new TextPromptDlg("Profile name", "Name for the new profile:", "Home", "Profile name"));
        yield return ("UpdateAvailable", () => new UpdateAvailableDlg("2.0.84", "A newer version is available.", "Notes line one.\r\nNotes line two."));
        yield return ("Wording", () => new WordingEditorDlg());
        yield return ("RankOrder", () => new RankOrderDlg(new List<WsjtxClient.RankMethods>(), null, new List<WsjtxClient.CallCategory>()));
        yield return ("RowDisplayOrder", () => new RowDisplayOrderDlg(null, null, null, false));
        yield return ("NotificationJoinOrder", () => new NotificationJoinOrderDlg(new List<NotificationEventType>()));
        yield return ("SupportReport", () => new SupportReportDlg(Ctrl(), "K1ABC"));
        yield return ("CompanionListEditor", () => new CompanionListEditorDlg());
        yield return ("NewRuleWizard", () => new NewRuleWizardDlg());
        yield return ("RuleDefinitionManager", () => new RuleDefinitionManagerDlg());
        yield return ("RuleDefinitionEdit", () => new RuleDefinitionEditDlg(null));
        yield return ("RestoreDefaultAwards", () => new RestoreDefaultAwardsDlg(12, new List<RuleDefinition>()));
        yield return ("BulkEdit", () => new BulkEditDlg(7));
        yield return ("EditLogRowOrder", () => new EditLogRowOrderDlg(null));
        yield return ("ExportSourceFilter", () => new ExportSourceFilterDlg());
        yield return ("EditQso", () => new EditQsoDlg(new QsoRecord { Callsign = "K1ABC", Band = "20m", Mode = "FT8" }));
        yield return ("ContactEdit", () => new ContactEditDlg(new NexusQso { Call = "K1ABC", Band = "20m", Mode = "FT8" }, (q, s) => null));
        yield return ("AlertRegions", () => new AlertRegionsDlg(new AlertRegions(), new List<ClubLogEntity>(), SystemFonts.MessageBoxFont));
        yield return ("LookupInfo", () => new LookupInfoDlg("K1ABC", null));
        yield return ("CallCq", () => { var c = new Controller(); Bring(c); return new CallCqDlg(c, typeof(Controller).GetField("wsjtxClient", Any)?.GetValue(c) as WsjtxClient); });
        yield return ("RxTxFreq", () => { var c = new Controller(); Bring(c); return new RxTxFreqDlg(c, typeof(Controller).GetField("wsjtxClient", Any)?.GetValue(c) as WsjtxClient); });
        yield return ("SupportInstall", () => new SupportInstallDlg("Install settings from support", new List<string> { "Radio: IC-7300", "Audio in: USB Audio CODEC" }, "Home", new List<SupportLocalChoice>()));
    }
}
