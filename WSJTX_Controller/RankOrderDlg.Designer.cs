using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    partial class RankOrderDlg
    {
        // ── Tab infrastructure ─────────────────────────────────────────────────
        private TabControl tabControl;
        private TabPage    callingPrioritiesTabPage;
        private TabPage    sortTabPage;
        private TabPage    helpTabPage;

        // ── Tab 1 – Priorities & Filters ───────────────────────────────────────
        // Single list: checkbox = admitted to the queue; position = both the display
        // priority tier AND Alt+N's category preference order. Previously these were
        // two separate tabs/lists (List Priorities, Call Filters) that could disagree
        // about a category's rank -- merged into one so they never can.
        private CheckedListBox   callingCheckedListBox;
        private Button           callingMoveUpButton;
        private Button           callingMoveDownButton;
        private Button           restoreCallingDefaultsButton;

        // ── Tab 2 – Normal Sort Order ──────────────────────────────────────────
        private CheckedListBox   sortCheckedListBox;
        private Button           sortMoveUpButton;
        private Button           sortMoveDownButton;
        private Label            beamStaticLabel;
        private ComboBox         beamComboBox;
        private Button           restoreSortDefaultsButton;

        // ── Tab 4 – Help ───────────────────────────────────────────────────────
        private TextBox          helpTextBox;

        // ── Form-level buttons ─────────────────────────────────────────────────
        private Button           okButton;
        private Button           cancelButton;

        private void InitializeComponent()
        {
            var font = new Font("Microsoft Sans Serif", 8.25F);

            this.tabControl               = new TabControl();
            this.callingPrioritiesTabPage = new TabPage();
            this.sortTabPage              = new TabPage();
            this.helpTabPage              = new TabPage();

            this.callingCheckedListBox        = new CheckedListBox();
            this.callingMoveUpButton          = new Button();
            this.callingMoveDownButton        = new Button();
            this.restoreCallingDefaultsButton = new Button();

            this.sortCheckedListBox        = new CheckedListBox();
            this.sortMoveUpButton          = new Button();
            this.sortMoveDownButton        = new Button();
            this.beamStaticLabel           = new Label();
            this.beamComboBox              = new ComboBox();
            this.restoreSortDefaultsButton = new Button();

            this.helpTextBox  = new TextBox();
            this.okButton     = new Button();
            this.cancelButton = new Button();

            this.tabControl.SuspendLayout();
            this.callingPrioritiesTabPage.SuspendLayout();
            this.sortTabPage.SuspendLayout();
            this.helpTabPage.SuspendLayout();
            this.SuspendLayout();

            // ══ TAB CONTROL ════════════════════════════════════════════════════
            this.tabControl.Controls.Add(this.callingPrioritiesTabPage);
            this.tabControl.Controls.Add(this.sortTabPage);
            this.tabControl.Controls.Add(this.helpTabPage);
            this.tabControl.Location      = new Point(8, 8);
            this.tabControl.Name          = "tabControl";
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size          = new Size(406, 306);
            this.tabControl.TabIndex      = 0;

            // ══ TAB 1 – PRIORITIES & FILTERS ═══════════════════════════════════
            this.callingPrioritiesTabPage.Controls.Add(this.callingCheckedListBox);
            this.callingPrioritiesTabPage.Controls.Add(this.callingMoveUpButton);
            this.callingPrioritiesTabPage.Controls.Add(this.callingMoveDownButton);
            this.callingPrioritiesTabPage.Controls.Add(this.restoreCallingDefaultsButton);
            this.callingPrioritiesTabPage.Name    = "callingPrioritiesTabPage";
            this.callingPrioritiesTabPage.Text    = "Priorities && Filters";
            this.callingPrioritiesTabPage.Padding = new Padding(6);
            this.callingPrioritiesTabPage.AccessibleName = "Priorities and Filters";

            // CheckedListBox – TabIndex 0
            this.callingCheckedListBox.CheckOnClick          = true;
            this.callingCheckedListBox.Font                  = font;
            this.callingCheckedListBox.FormattingEnabled     = true;
            this.callingCheckedListBox.Location              = new Point(8, 8);
            this.callingCheckedListBox.Name                  = "callingCheckedListBox";
            this.callingCheckedListBox.Size                  = new Size(384, 160);
            this.callingCheckedListBox.TabIndex              = 0;
            this.callingCheckedListBox.AccessibleName        = "Priority and filter categories";
            this.callingCheckedListBox.SelectedIndexChanged += new EventHandler(this.CallingCheckedListBox_SelectedIndexChanged);

            // Move Up – TabIndex 1
            this.callingMoveUpButton.Font                  = font;
            this.callingMoveUpButton.Location              = new Point(8, 176);
            this.callingMoveUpButton.Name                  = "callingMoveUpButton";
            this.callingMoveUpButton.Size                  = new Size(182, 26);
            this.callingMoveUpButton.TabIndex              = 1;
            this.callingMoveUpButton.Text                  = "Move Up";
            this.callingMoveUpButton.UseVisualStyleBackColor = true;
            this.callingMoveUpButton.AccessibleName        = "Move Up";
            this.callingMoveUpButton.Click                += new EventHandler(this.CallingMoveUpButton_Click);

            // Move Down – TabIndex 2
            this.callingMoveDownButton.Font                  = font;
            this.callingMoveDownButton.Location              = new Point(198, 176);
            this.callingMoveDownButton.Name                  = "callingMoveDownButton";
            this.callingMoveDownButton.Size                  = new Size(182, 26);
            this.callingMoveDownButton.TabIndex              = 2;
            this.callingMoveDownButton.Text                  = "Move Down";
            this.callingMoveDownButton.UseVisualStyleBackColor = true;
            this.callingMoveDownButton.AccessibleName        = "Move Down";
            this.callingMoveDownButton.Click                += new EventHandler(this.CallingMoveDownButton_Click);

            // Restore Defaults – TabIndex 3
            this.restoreCallingDefaultsButton.Font                  = font;
            this.restoreCallingDefaultsButton.Location              = new Point(8, 210);
            this.restoreCallingDefaultsButton.Name                  = "restoreCallingDefaultsButton";
            this.restoreCallingDefaultsButton.Size                  = new Size(182, 26);
            this.restoreCallingDefaultsButton.TabIndex              = 3;
            this.restoreCallingDefaultsButton.Text                  = "Restore Defaults";
            this.restoreCallingDefaultsButton.UseVisualStyleBackColor = true;
            this.restoreCallingDefaultsButton.AccessibleName        = "Restore Defaults";
            this.restoreCallingDefaultsButton.Click                += new EventHandler(this.RestoreCallingDefaultsButton_Click);

            // ══ TAB 2 – NORMAL SORT ORDER ══════════════════════════════════════
            this.sortTabPage.Controls.Add(this.sortCheckedListBox);
            this.sortTabPage.Controls.Add(this.sortMoveUpButton);
            this.sortTabPage.Controls.Add(this.sortMoveDownButton);
            this.sortTabPage.Controls.Add(this.beamStaticLabel);
            this.sortTabPage.Controls.Add(this.beamComboBox);
            this.sortTabPage.Controls.Add(this.restoreSortDefaultsButton);
            this.sortTabPage.Name    = "sortTabPage";
            this.sortTabPage.Text    = "Normal Sort Order";
            this.sortTabPage.Padding = new Padding(6);
            this.sortTabPage.AccessibleName = "Normal Sort Order";

            // Sort methods list – TabIndex 0
            this.sortCheckedListBox.CheckOnClick          = true;
            this.sortCheckedListBox.Font                  = font;
            this.sortCheckedListBox.FormattingEnabled     = true;
            this.sortCheckedListBox.Location              = new Point(8, 8);
            this.sortCheckedListBox.Name                  = "sortCheckedListBox";
            this.sortCheckedListBox.Size                  = new Size(384, 110);
            this.sortCheckedListBox.TabIndex              = 0;
            this.sortCheckedListBox.AccessibleName        = "Sort methods";
            this.sortCheckedListBox.SelectedIndexChanged += new EventHandler(this.SortCheckedListBox_SelectedIndexChanged);

            // Move Up – TabIndex 1
            this.sortMoveUpButton.Font                  = font;
            this.sortMoveUpButton.Location              = new Point(8, 126);
            this.sortMoveUpButton.Name                  = "sortMoveUpButton";
            this.sortMoveUpButton.Size                  = new Size(182, 26);
            this.sortMoveUpButton.TabIndex              = 1;
            this.sortMoveUpButton.Text                  = "Move Up";
            this.sortMoveUpButton.UseVisualStyleBackColor = true;
            this.sortMoveUpButton.AccessibleName        = "Move Up";
            this.sortMoveUpButton.Click                += new EventHandler(this.SortMoveUpButton_Click);

            // Move Down – TabIndex 2
            this.sortMoveDownButton.Font                  = font;
            this.sortMoveDownButton.Location              = new Point(198, 126);
            this.sortMoveDownButton.Name                  = "sortMoveDownButton";
            this.sortMoveDownButton.Size                  = new Size(182, 26);
            this.sortMoveDownButton.TabIndex              = 2;
            this.sortMoveDownButton.Text                  = "Move Down";
            this.sortMoveDownButton.UseVisualStyleBackColor = true;
            this.sortMoveDownButton.AccessibleName        = "Move Down";
            this.sortMoveDownButton.Click                += new EventHandler(this.SortMoveDownButton_Click);

            // Beam label (no tab stop)
            this.beamStaticLabel.AutoSize       = true;
            this.beamStaticLabel.Font           = font;
            this.beamStaticLabel.Location       = new Point(8, 164);
            this.beamStaticLabel.Name           = "beamStaticLabel";
            this.beamStaticLabel.TabStop        = false;
            this.beamStaticLabel.Text           = "Beam preference:";

            // Beam combo – TabIndex 3
            this.beamComboBox.DropDownStyle     = ComboBoxStyle.DropDownList;
            this.beamComboBox.Font              = font;
            this.beamComboBox.FormattingEnabled = true;
            this.beamComboBox.Location          = new Point(120, 160);
            this.beamComboBox.Name              = "beamComboBox";
            this.beamComboBox.Size              = new Size(100, 21);
            this.beamComboBox.TabIndex          = 3;
            this.beamComboBox.AccessibleName    = "Beam preference";

            // Restore Defaults – TabIndex 4
            this.restoreSortDefaultsButton.Font                  = font;
            this.restoreSortDefaultsButton.Location              = new Point(8, 192);
            this.restoreSortDefaultsButton.Name                  = "restoreSortDefaultsButton";
            this.restoreSortDefaultsButton.Size                  = new Size(182, 26);
            this.restoreSortDefaultsButton.TabIndex              = 4;
            this.restoreSortDefaultsButton.Text                  = "Restore Defaults";
            this.restoreSortDefaultsButton.UseVisualStyleBackColor = true;
            this.restoreSortDefaultsButton.AccessibleName        = "Restore Defaults";
            this.restoreSortDefaultsButton.Click                += new EventHandler(this.RestoreSortDefaultsButton_Click);

            // ══ TAB 3 – HELP ══════════════════════════════════════════════════
            this.helpTabPage.Controls.Add(this.helpTextBox);
            this.helpTabPage.Name    = "helpTabPage";
            this.helpTabPage.Text    = "Help";
            this.helpTabPage.Padding = new Padding(6);
            this.helpTabPage.AccessibleName = "Help";

            this.helpTextBox.Multiline      = true;
            this.helpTextBox.ReadOnly       = true;
            this.helpTextBox.ScrollBars     = ScrollBars.Vertical;
            this.helpTextBox.BorderStyle    = BorderStyle.None;
            this.helpTextBox.BackColor      = SystemColors.Control;
            this.helpTextBox.Font           = font;
            this.helpTextBox.Location       = new Point(8, 8);
            this.helpTextBox.Name           = "helpTextBox";
            this.helpTextBox.Size           = new Size(384, 260);
            this.helpTextBox.TabIndex       = 0;
            this.helpTextBox.TabStop        = true;
            this.helpTextBox.AccessibleName = "Help notes";
            this.helpTextBox.Text =
                "Priorities & Filters\r\n" +
                "────────────────────\r\n" +
                "One list controls three things together, so they can never disagree:\r\n" +
                "  1. Which categories are admitted to the waiting queue.\r\n" +
                "  2. How categories are ranked in the stations available list.\r\n" +
                "  3. Which category Alt+N prefers when several are waiting.\r\n" +
                "\r\n" +
                "Checked = admitted to the queue AND elevated above unchecked\r\n" +
                "categories and ordinary calls. Unchecked = decoded and classified,\r\n" +
                "but never admitted to TX1/TX2/RX1/RX2, and never picked by Alt+N.\r\n" +
                "\r\n" +
                "Move categories up or down to set their relative priority — the\r\n" +
                "order here is both the display rank and the Alt+N preference order.\r\n" +
                "\r\n" +
                "Example:\r\n" +
                "  New DXCC above Calling Me → New DXCC calls are shown first\r\n" +
                "  and Alt+N jumps to a New DXCC call before a Calling Me call.\r\n" +
                "\r\n" +
                "Every decoded station is always fully classified before the\r\n" +
                "filter is applied — Jimmy never misses a decode.\r\n" +
                "\r\n" +
                "Directed CQ: also requires a matching entry in the\r\n" +
                "\"Queue directed CQ calls for:\" text field (e.g. POTA SOTA).\r\n" +
                "\r\n" +
                "Ordinary CQ: also subject to the DX / Local origin filter\r\n" +
                "and band-scope setting on the Receive tab.\r\n" +
                "\r\n" +
                "Calling Me: always enters the waiting queue regardless of\r\n" +
                "this checkbox — Jimmy never hides a station calling you.\r\n" +
                "The checkbox controls only whether Alt+N will jump to a\r\n" +
                "station that is calling you.\r\n" +
                "\r\n" +
                "Alt+N picks the highest-ranked eligible admitted call.\r\n" +
                "\r\n" +
                "Normal Sort Order\r\n" +
                "─────────────────\r\n" +
                "Controls how calls within the same priority tier are sorted.\r\n" +
                "Check one or more methods. The first checked item is the\r\n" +
                "primary sort within a tier.\r\n" +
                "Beam preference favors callers in a compass direction.\r\n" +
                "\r\n" +
                "Keyboard shortcuts\r\n" +
                "──────────────────\r\n" +
                "Space or Enter — call the selected row in the call list.\r\n" +
                "Alt+N — call the best eligible category call.";

            // ══ FORM-LEVEL BUTTONS ═════════════════════════════════════════════
            // OK – TabIndex 1
            this.okButton.Font                  = font;
            this.okButton.Location              = new Point(246, 322);
            this.okButton.Name                  = "okButton";
            this.okButton.Size                  = new Size(78, 28);
            this.okButton.TabIndex              = 1;
            this.okButton.Text                  = "OK";
            this.okButton.UseVisualStyleBackColor = true;
            this.okButton.AccessibleName        = "OK";
            this.okButton.Click                += new EventHandler(this.OkButton_Click);

            // Cancel – TabIndex 2
            this.cancelButton.Font                  = font;
            this.cancelButton.Location              = new Point(330, 322);
            this.cancelButton.Name                  = "cancelButton";
            this.cancelButton.Size                  = new Size(78, 28);
            this.cancelButton.TabIndex              = 2;
            this.cancelButton.Text                  = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.AccessibleName        = "Cancel";
            this.cancelButton.DialogResult          = DialogResult.Cancel;

            // ══ FORM ═══════════════════════════════════════════════════════════
            this.AcceptButton    = this.okButton;
            this.CancelButton    = this.cancelButton;
            this.ClientSize      = new Size(422, 360);
            this.Controls.Add(this.tabControl);
            this.Controls.Add(this.okButton);
            this.Controls.Add(this.cancelButton);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox     = false;
            this.MinimizeBox     = false;
            this.Name            = "RankOrderDlg";
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Text            = "Stations Available Sort Order";
            this.AccessibleName  = "Stations Available Sort Order";
            this.ShowInTaskbar   = false;

            this.helpTabPage.ResumeLayout(false);
            this.sortTabPage.ResumeLayout(false);
            this.sortTabPage.PerformLayout();
            this.callingPrioritiesTabPage.ResumeLayout(false);
            this.tabControl.ResumeLayout(false);
            this.ResumeLayout(false);
        }
    }
}
