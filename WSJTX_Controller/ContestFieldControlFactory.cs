using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // Nexus contesting foundation, phase 6: maps a ContestField's Kind (from
    // ContestClient.GetRuleset -- Nexus's own real exchange-field shapes, never a Jimmy-side
    // guess) to one accessible WinForms control. The whole point: adding a new contest never
    // requires a new Options/Contesting-page layout, because every field this factory can
    // receive is rendered the same generic way regardless of which contest it came from.
    public static class ContestFieldControlFactory
    {
        // Returns a single Control the caller positions/sizes itself (matches every other
        // Build*Tab method's own plain-construction style, e.g. BuildStationOperatorTab). For a
        // ComboBox (Enum kind), Items are pre-populated from the field's own resolved domain
        // values -- never a Jimmy-owned copy of section/state/county lists.
        public static Control Create(ContestField field, Font font)
        {
            var kind = field.Kind;
            string type = kind?.Type ?? "Text";

            switch (type)
            {
                case "Enum":
                    {
                        var combo = new ComboBox
                        {
                            DropDownStyle = ComboBoxStyle.DropDown,
                            Font = font,
                            AccessibleName = field.Label ?? field.Key,
                        };
                        foreach (var v in kind.Domain?.Values ?? new System.Collections.Generic.List<ContestDomainValue>())
                            combo.Items.Add($"{v.Code} - {v.Name}");
                        return combo;
                    }
                case "OneOf":
                    // Render the first arm's control -- a documented simplification for phase 6
                    // (a OneOf slot, e.g. county-or-state-or-DX, is rare outside contests not yet
                    // wired for automation). The operator's typed value still round-trips as free
                    // text; which arm it matched is Nexus's own ExchangeSpec::copied verdict at
                    // validation time, never guessed here.
                    return field.Kind?.Arms?.Count > 0
                        ? Create(new ContestField { Key = field.Key, Label = field.Label, Required = field.Required, Kind = field.Kind.Arms[0] }, font)
                        : MakeTextBox(field, font, maxLength: 20);
                case "Number":
                    {
                        var num = new NumericUpDown
                        {
                            Font = font,
                            AccessibleName = field.Label ?? field.Key,
                            Minimum = kind.Min ?? 0,
                            Maximum = kind.Max ?? 999999,
                        };
                        return num;
                    }
                case "Rst":
                    return MakeTextBox(field, font, maxLength: kind.Digits ?? 3);
                case "Grid":
                    return MakeTextBox(field, font, maxLength: kind.Chars ?? 6);
                case "Serial":
                    return new Label
                    {
                        AutoSize = true,
                        Font = font,
                        Text = "(assigned automatically)",
                        AccessibleName = (field.Label ?? field.Key) + ", assigned automatically",
                    };
                case "Call":
                    return MakeTextBox(field, font, maxLength: 15);
                case "Pattern":
                case "Text":
                default:
                    return MakeTextBox(field, font, maxLength: kind?.MaxLen ?? 40);
            }
        }

        private static TextBox MakeTextBox(ContestField field, Font font, int maxLength)
        {
            return new TextBox
            {
                Font = font,
                AccessibleName = field.Label ?? field.Key,
                MaxLength = Math.Max(1, maxLength),
            };
        }

        // Reads back whatever the operator entered/selected, normalized to the raw code Nexus
        // expects (an Enum ComboBox shows "CODE - Name"; only CODE is sent -- Nexus's own
        // ExchangeSpec::copied does the actual validation, this just extracts what was typed).
        public static string ReadValue(Control control)
        {
            switch (control)
            {
                case ComboBox combo:
                    {
                        string text = combo.Text ?? "";
                        int dash = text.IndexOf(" - ", StringComparison.Ordinal);
                        return (dash > 0 ? text.Substring(0, dash) : text).Trim().ToUpperInvariant();
                    }
                case NumericUpDown num:
                    return num.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case TextBox tb:
                    return tb.Text.Trim().ToUpperInvariant();
                default:
                    return "";
            }
        }

        // Prefills a control with a previously-saved value (ContestConfigStore's own per-contest
        // entry defaults) -- the write-side counterpart to ReadValue above. A no-op for an empty/
        // absent saved default (e.g. a contest entered for the first time) so a blank field is
        // never overwritten with an empty string the operator would then have to notice and clear.
        public static void WriteValue(Control control, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            switch (control)
            {
                case ComboBox combo:
                    combo.Text = value;
                    break;
                case NumericUpDown num:
                    if (decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                        num.Value = Math.Max(num.Minimum, Math.Min(num.Maximum, d));
                    break;
                case TextBox tb:
                    tb.Text = value;
                    break;
            }
        }
    }
}
