using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Media;

namespace WSJTX_Controller
{
    public partial class ConfirmDlg : Form
    {
        public string text;

        public ConfirmDlg()
        {
            InitializeComponent();
            text = "";
        }

        private void ConfirmDlg_FormClosing(object sender, FormClosingEventArgs e)
        {

        }

        private void ConfirmDlg_Load(object sender, EventArgs e)
        {
            // Release-audit finding, 2026-08-20 (release blocker): this dialog's own caption was
            // never set anywhere (every call site only ever sets the message body via `text`),
            // and focus used to land on yesButton first -- a JAWS/NVDA user heard only "Yes
            // button" with no indication of what they were being asked. AcceptButton/
            // CancelButton (ConfirmDlg.Designer.cs) still make Enter/Escape answer Yes/No
            // regardless of focus, so it's safe to focus the actual question text instead.
            Text = "Confirm";
            panel1.BackgroundImage = Bitmap.FromHicon(SystemIcons.Question.Handle);
            panel1.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Center;
            textBox.Text = text;
            textBox.SelectionStart = 0;
            textBox.SelectionLength = 0;
            textBox.Focus();
            var pt = Owner.Location;
            pt.Offset(new Point((int)((Owner.Width - Width) / 2), (int)(Owner.Height / 5)));
            Location = pt;
        }

        private void ConfirmDlg_FormClosed(object sender, FormClosedEventArgs e)
        {
           
        }

        private void yesButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Yes;
        }

        private void nobutton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.No;
        }

    }
}
