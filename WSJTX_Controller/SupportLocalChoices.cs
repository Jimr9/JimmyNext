using System;
using System.Collections.Generic;
using System.Linq;

namespace WSJTX_Controller
{
    // The settings that belong to THIS computer when support settings are installed (2026-10-12):
    // the radio's CAT port, its baud rate, a separate PTT port, and the sound devices. A COM
    // number or a device name that matches the helper's proves nothing about the hardware, so
    // each is shown three ways -- what the settings carry, what this computer has now, and what
    // WILL be used -- and nothing changes unless the operator chooses it. Sound devices never
    // travel in support settings; they are only ever chosen here.
    //
    // This class decides the choices and checks them; it opens no port and no device, and makes
    // no window, so the rules can be tested as they are. SupportInstallDlg shows them.
    internal sealed class SupportLocalChoice
    {
        internal const string CatPort = "radioComPort", Baud = "radioBaudRate", PttPort = "radioPttSerialPort",
            AudioIn = "nativeEngineAudioDevice", AudioOut = "nativeEngineAudioOutputDevice";
        internal static readonly string[] Keys = { CatPort, Baud, PttPort, AudioIn, AudioOut };
        internal static readonly string[] StandardBauds = { "1200", "4800", "9600", "19200", "38400", "57600", "115200" };

        internal sealed class Option
        {
            public string Text;     // what is read out
            public string Value;    // the value to write; null = keep this computer's own
        }

        public string Key, Name;
        public string Mine = "";           // this computer's value now ("" = none set)
        public bool InSettings;            // the support settings carry a value for it
        public string Theirs = "";         // that value ("" with InSettings = an explicit blank)
        public bool Required;              // the installed setup cannot work with none
        public bool Checked = true;        // false: this computer's ports/devices could not be listed
        public List<Option> Options = new List<Option>();
        public int Default;
        public string Note;                // one line said beside the choice, or null

        // What will be in effect with option `i` chosen.
        internal string Effective(int i) => Options[i].Value ?? Mine;

        // "The settings use COM5. This computer uses COM3." -- for the screen reader, on the choice.
        internal string Comparison()
        {
            string theirs = Key == AudioIn || Key == AudioOut ? "Sound devices are not part of support settings; choose on this computer."
                : !InSettings ? "The settings do not include it."
                : Theirs.Length == 0 ? (Key == Baud ? "The settings use the radio driver's default rate." : Key == PttPort ? "The settings use the CAT port for PTT." : "The settings leave it empty.")
                : $"The settings use {Theirs}.";
            string mine = Mine.Length == 0 ? (Key == Baud ? "This computer uses the radio driver's default rate." : Key == PttPort ? "This computer uses the CAT port for PTT." : "This computer has none set.")
                : $"This computer uses {Mine}.";
            return theirs + " " + mine + (Note == null ? "" : " " + Note);
        }

        // Why option `i` cannot be installed, or null.
        internal string Problem(int i)
        {
            if (!Required || Effective(i).Length > 0) return null;
            return Checked || Options.Count > 1
                ? $"{Name}: none is set. Choose the one your radio uses."
                : $"{Name}: none is set, and this computer's list could not be read. Check the radio and its cable, then try again.";
        }

        // What the installed settings will need, from the support settings themselves (a setting
        // they leave out goes back to its default when they are installed).
        internal sealed class Needs
        {
            public bool SerialCat;      // Hamlib through Jimmy's own rigctld on a COM port
            public bool SerialPtt;      // PTT on a serial line (RTS or DTR), so a PTT port means something
            public bool Radio;          // a radio is set up at all, so sound devices are needed
            public string RigModel = "";
        }

        internal static Needs NeedsOf(IDictionary<string, string> settings)
        {
            string Get(string k) => settings != null && settings.TryGetValue(k, out string v) ? (v ?? "").Trim() : "";
            var d = new RadioSettings();
            RadioControlMode mode = Enum.TryParse(Get("radioControlMode"), out RadioControlMode m) ? m : d.Mode;
            bool external = settings != null && settings.ContainsKey("radioUseExternalRigctld") ? Get("radioUseExternalRigctld") == "True" : d.UseExternalRigctld;
            PttMethod ptt = Enum.TryParse(Get("radioPttMethod"), out PttMethod p) ? p : d.PttMethod;
            string model = settings != null && settings.ContainsKey("radioRigModel") ? Get("radioRigModel") : d.RigModel;
            bool hamlib = mode == RadioControlMode.HamlibRigctld && model.Length > 0;
            return new Needs
            {
                SerialCat = hamlib && !external,
                SerialPtt = hamlib && (ptt == PttMethod.SerialRts || ptt == PttMethod.SerialDtr),
                Radio = hamlib,
                RigModel = model,
            };
        }

        // The choices for one install. mine: this computer's five values now. baseline: the five
        // this computer was last set up with through these very settings (an offline restore), or
        // null. ports / audioIn / audioOut: what this computer has; null = could not be listed.
        internal static List<SupportLocalChoice> Plan(IDictionary<string, string> settings, Func<string, string> mine,
            IDictionary<string, string> baseline, IList<string> ports, IList<string> audioIn, IList<string> audioOut, string myRigModel)
        {
            var needs = NeedsOf(settings);
            var list = new List<SupportLocalChoice>();
            string Mine(string k) => (mine?.Invoke(k) ?? "").Trim();
            string Base(string k) => baseline != null && baseline.TryGetValue(k, out string v) ? (v ?? "").Trim() : null;
            bool Has(IList<string> have, string v) => have != null && have.Contains(v, StringComparer.OrdinalIgnoreCase);

            SupportLocalChoice Start(string key, string name, IList<string> have, bool required)
            {
                var c = new SupportLocalChoice { Key = key, Name = name, Mine = Mine(key), Required = required, Checked = have != null };
                c.InSettings = settings != null && settings.ContainsKey(key);
                c.Theirs = c.InSettings ? (settings[key] ?? "").Trim() : "";
                return c;
            }
            // A port or device: keep mine first, then everything this computer has.
            void Devices(SupportLocalChoice c, IList<string> have, string kind)
            {
                bool mineHere = c.Mine.Length > 0 && Has(have, c.Mine);
                c.Options.Add(new Option
                {
                    Value = null,
                    Text = c.Mine.Length == 0 ? "Keep mine: none set"
                         : have != null && !mineHere ? $"Keep mine: {c.Mine}, not available now"
                         : $"Keep mine: {c.Mine}",
                });
                foreach (string h in have ?? new List<string>())
                    c.Options.Add(new Option { Value = h, Text = c.InSettings && string.Equals(h, c.Theirs, StringComparison.OrdinalIgnoreCase) ? $"{h}, as in the settings" : h });
                if (have == null) c.Note = $"This computer's {kind} could not be listed.";
                else if (c.InSettings && c.Theirs.Length > 0 && !Has(have, c.Theirs)) c.Note = $"This computer has no {c.Theirs}.";
                // The value this computer installed with before comes first; else its own stays.
                // The settings' own port is never taken just because this computer has one of
                // that name.
                string b = Base(c.Key);
                int at = b != null && b.Length > 0 && !string.Equals(b, c.Mine, StringComparison.OrdinalIgnoreCase)
                    ? c.Options.FindIndex(o => o.Value != null && string.Equals(o.Value, b, StringComparison.OrdinalIgnoreCase)) : -1;
                if (b != null && b.Length > 0 && at < 0 && !string.Equals(b, c.Mine, StringComparison.OrdinalIgnoreCase))
                    c.Note = (c.Note == null ? "" : c.Note + " ") + $"It was installed with {b}, which is not available now.";
                c.Default = Math.Max(0, at);
            }

            if (needs.SerialCat)
            {
                var cat = Start(CatPort, "Radio CAT port", ports, required: true);
                Devices(cat, ports, "serial ports");
                list.Add(cat);

                // The baud rate must match the radio, whatever the port is called.
                var baud = Start(Baud, "Radio baud rate", StandardBauds, required: false);
                baud.Options.Add(new Option { Value = null, Text = baud.Mine.Length == 0 ? "Keep mine: the radio driver's default" : $"Keep mine: {baud.Mine}" });
                if (baud.InSettings && !string.Equals(baud.Theirs, baud.Mine, StringComparison.OrdinalIgnoreCase))
                    baud.Options.Add(new Option { Value = baud.Theirs, Text = baud.Theirs.Length == 0 ? "As in the settings: the radio driver's default" : $"As in the settings: {baud.Theirs}" });
                baud.Options.Add(new Option { Value = "", Text = "The radio driver's default" });
                foreach (string r in StandardBauds) baud.Options.Add(new Option { Value = r, Text = r });
                string bb = Base(Baud);
                int at = -1;
                if (bb != null && !string.Equals(bb, baud.Mine, StringComparison.OrdinalIgnoreCase))
                    at = baud.Options.FindIndex(o => o.Value != null && string.Equals(o.Value, bb, StringComparison.OrdinalIgnoreCase));
                else if (bb == null && baud.InSettings && !string.Equals(baud.Theirs, baud.Mine, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(needs.RigModel, (myRigModel ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // A different radio model comes with these settings: its rate comes with it,
                    // never this computer's rate for another radio. Said, so it is no surprise.
                    at = 1;
                    baud.Note = "The settings are for a different radio model, so their rate is chosen.";
                }
                baud.Default = Math.Max(0, at);
                list.Add(baud);
            }
            if (needs.SerialPtt)
            {
                var ptt = Start(PttPort, "PTT port", ports, required: false);   // empty = the CAT port
                Devices(ptt, ports, "serial ports");
                ptt.Options[0].Text = ptt.Mine.Length == 0 ? "Keep mine: the CAT port" : ptt.Options[0].Text;
                ptt.Options.Insert(1, new Option { Value = "", Text = "The CAT port" });
                if (ptt.Default >= 1) ptt.Default++;
                list.Add(ptt);
            }
            var input = Start(AudioIn, "Sound input, from the radio", audioIn, required: needs.Radio);
            Devices(input, audioIn, "sound devices");
            list.Add(input);
            var output = Start(AudioOut, "Sound output, to the radio", audioOut, required: needs.Radio);
            Devices(output, audioOut, "sound devices");
            list.Add(output);
            return list;
        }
    }
}
