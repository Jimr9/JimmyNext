using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    // "Show password" (operator, 2026-10-08): a check box right after a password box, on the same
    // line, that shows what was typed while checked. A check box rather than a button so a screen
    // reader says whether the password is showing. Same Tab index as the box and added after it,
    // so Tab reaches it straight after the password. `also`: other boxes it shows too ("Password
    // again").
    internal static class PasswordReveal
    {
        internal static CheckBox Attach(TextBox box, params TextBox[] also)
        {
            var boxes = new TextBox[also.Length + 1];
            boxes[0] = box;
            also.CopyTo(boxes, 1);
            var hidden = new char[boxes.Length];
            var system = new bool[boxes.Length];
            for (int i = 0; i < boxes.Length; i++) { hidden[i] = boxes[i].PasswordChar; system[i] = boxes[i].UseSystemPasswordChar; }

            var show = new CheckBox
            {
                Text = "Show password",
                AccessibleName = "Show password",
                AutoSize = true,
                Font = box.Font,
                Location = new Point(box.Right + 8, box.Top + 1),
                TabIndex = box.TabIndex,
            };
            show.CheckedChanged += (s, e) =>
            {
                for (int i = 0; i < boxes.Length; i++)
                {
                    boxes[i].UseSystemPasswordChar = !show.Checked && system[i];
                    boxes[i].PasswordChar = show.Checked ? '\0' : hidden[i];
                }
            };
            box.Parent?.Controls.Add(show);
            return show;
        }
    }
}
