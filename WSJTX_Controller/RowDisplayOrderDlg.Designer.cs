using System;
using System.Drawing;
using System.Windows.Forms;

namespace WSJTX_Controller
{
    partial class RowDisplayOrderDlg
    {
        private TabControl tabControl;
        private TabPage callWaitingTabPage;
        private TabPage rawDecodeTabPage;
        private TabPage spotWatchTabPage;

        private Label callWaitingInstructionsLabel;
        private CheckedListBox callWaitingListBox;
        private Button callWaitingMoveUpButton;
        private Button callWaitingMoveDownButton;
        private Button callWaitingRestoreDefaultButton;

        private Label rawDecodeInstructionsLabel;
        private CheckedListBox rawDecodeListBox;
        private Button rawDecodeMoveUpButton;
        private Button rawDecodeMoveDownButton;
        private Button rawDecodeRestoreDefaultButton;

        private Label spotWatchInstructionsLabel;
        private CheckedListBox spotWatchListBox;
        private Button spotWatchMoveUpButton;
        private Button spotWatchMoveDownButton;
        private Button spotWatchRestoreDefaultButton;

        private Button okButton;
        private Button cancelButton;

        private void InitializeComponent()
        {
            this.tabControl = new TabControl();
            this.callWaitingTabPage = new TabPage();
            this.rawDecodeTabPage = new TabPage();
            this.spotWatchTabPage = new TabPage();

            this.callWaitingInstructionsLabel = new Label();
            this.callWaitingListBox = new CheckedListBox();
            this.callWaitingMoveUpButton = new Button();
            this.callWaitingMoveDownButton = new Button();
            this.callWaitingRestoreDefaultButton = new Button();

            this.rawDecodeInstructionsLabel = new Label();
            this.rawDecodeListBox = new CheckedListBox();
            this.rawDecodeMoveUpButton = new Button();
            this.rawDecodeMoveDownButton = new Button();
            this.rawDecodeRestoreDefaultButton = new Button();

            this.spotWatchInstructionsLabel = new Label();
            this.spotWatchListBox = new CheckedListBox();
            this.spotWatchMoveUpButton = new Button();
            this.spotWatchMoveDownButton = new Button();
            this.spotWatchRestoreDefaultButton = new Button();

            this.okButton = new Button();
            this.cancelButton = new Button();
            this.SuspendLayout();

            //
            // callWaitingInstructionsLabel
            //
            this.callWaitingInstructionsLabel.AutoSize = true;
            this.callWaitingInstructionsLabel.Location = new Point(8, 8);
            this.callWaitingInstructionsLabel.Size = new Size(314, 13);
            this.callWaitingInstructionsLabel.Text = "Checked fields are shown; use Move Up/Move Down to change display order.";
            this.callWaitingInstructionsLabel.AccessibleName = "Stations available row editor instructions";
            //
            // callWaitingListBox
            //
            this.callWaitingListBox.FormattingEnabled = true;
            this.callWaitingListBox.Location = new Point(8, 28);
            this.callWaitingListBox.Size = new Size(336, 190);
            this.callWaitingListBox.TabIndex = 0;
            this.callWaitingListBox.CheckOnClick = true;
            this.callWaitingListBox.AccessibleName = "Stations Available Row Fields List Box";
            this.callWaitingListBox.AccessibleDescription = "List of possible fields for stations available rows. Check fields to show them and uncheck fields to hide them. Select an item and use Move Up or Move Down to reorder.";
            this.callWaitingListBox.SelectedIndexChanged += new EventHandler(this.CallWaitingListBox_SelectedIndexChanged);
            //
            // callWaitingMoveUpButton
            //
            this.callWaitingMoveUpButton.Location = new Point(8, 224);
            this.callWaitingMoveUpButton.Size = new Size(100, 28);
            this.callWaitingMoveUpButton.TabIndex = 1;
            this.callWaitingMoveUpButton.Text = "Move Up";
            this.callWaitingMoveUpButton.UseVisualStyleBackColor = true;
            this.callWaitingMoveUpButton.AccessibleName = "Move selected field up";
            this.callWaitingMoveUpButton.Click += new EventHandler(this.CallWaitingMoveUpButton_Click);
            //
            // callWaitingMoveDownButton
            //
            this.callWaitingMoveDownButton.Location = new Point(114, 224);
            this.callWaitingMoveDownButton.Size = new Size(100, 28);
            this.callWaitingMoveDownButton.TabIndex = 2;
            this.callWaitingMoveDownButton.Text = "Move Down";
            this.callWaitingMoveDownButton.UseVisualStyleBackColor = true;
            this.callWaitingMoveDownButton.AccessibleName = "Move selected field down";
            this.callWaitingMoveDownButton.Click += new EventHandler(this.CallWaitingMoveDownButton_Click);
            //
            // callWaitingRestoreDefaultButton
            //
            this.callWaitingRestoreDefaultButton.Location = new Point(220, 224);
            this.callWaitingRestoreDefaultButton.Size = new Size(124, 28);
            this.callWaitingRestoreDefaultButton.TabIndex = 3;
            this.callWaitingRestoreDefaultButton.Text = "Restore Default";
            this.callWaitingRestoreDefaultButton.UseVisualStyleBackColor = true;
            this.callWaitingRestoreDefaultButton.AccessibleName = "Restore default order for Stations Available rows";
            this.callWaitingRestoreDefaultButton.Click += new EventHandler(this.CallWaitingRestoreDefaultButton_Click);
            //
            // callWaitingTabPage
            //
            this.callWaitingTabPage.Controls.Add(this.callWaitingInstructionsLabel);
            this.callWaitingTabPage.Controls.Add(this.callWaitingListBox);
            this.callWaitingTabPage.Controls.Add(this.callWaitingMoveUpButton);
            this.callWaitingTabPage.Controls.Add(this.callWaitingMoveDownButton);
            this.callWaitingTabPage.Controls.Add(this.callWaitingRestoreDefaultButton);
            this.callWaitingTabPage.Text = "Stations Available Row";
            this.callWaitingTabPage.AccessibleName = "Stations Available Row";
            this.callWaitingTabPage.UseVisualStyleBackColor = true;

            //
            // rawDecodeInstructionsLabel
            //
            this.rawDecodeInstructionsLabel.AutoSize = true;
            this.rawDecodeInstructionsLabel.Location = new Point(8, 8);
            this.rawDecodeInstructionsLabel.Size = new Size(314, 13);
            this.rawDecodeInstructionsLabel.Text = "Checked fields are shown; use Move Up/Move Down to change display order.";
            this.rawDecodeInstructionsLabel.AccessibleName = "Raw Decodes row editor instructions";
            //
            // rawDecodeListBox
            //
            this.rawDecodeListBox.FormattingEnabled = true;
            this.rawDecodeListBox.Location = new Point(8, 28);
            this.rawDecodeListBox.Size = new Size(336, 190);
            this.rawDecodeListBox.TabIndex = 0;
            this.rawDecodeListBox.CheckOnClick = true;
            this.rawDecodeListBox.AccessibleName = "Raw Decodes Row Fields List Box";
            this.rawDecodeListBox.AccessibleDescription = "List of possible fields for Raw Decodes rows. Check fields to show them and uncheck fields to hide them. Select an item and use Move Up or Move Down to reorder. Put Call Sign first so first-letter navigation in the Raw Decodes list works.";
            this.rawDecodeListBox.SelectedIndexChanged += new EventHandler(this.RawDecodeListBox_SelectedIndexChanged);
            //
            // rawDecodeMoveUpButton
            //
            this.rawDecodeMoveUpButton.Location = new Point(8, 224);
            this.rawDecodeMoveUpButton.Size = new Size(100, 28);
            this.rawDecodeMoveUpButton.TabIndex = 1;
            this.rawDecodeMoveUpButton.Text = "Move Up";
            this.rawDecodeMoveUpButton.UseVisualStyleBackColor = true;
            this.rawDecodeMoveUpButton.AccessibleName = "Move selected field up";
            this.rawDecodeMoveUpButton.Click += new EventHandler(this.RawDecodeMoveUpButton_Click);
            //
            // rawDecodeMoveDownButton
            //
            this.rawDecodeMoveDownButton.Location = new Point(114, 224);
            this.rawDecodeMoveDownButton.Size = new Size(100, 28);
            this.rawDecodeMoveDownButton.TabIndex = 2;
            this.rawDecodeMoveDownButton.Text = "Move Down";
            this.rawDecodeMoveDownButton.UseVisualStyleBackColor = true;
            this.rawDecodeMoveDownButton.AccessibleName = "Move selected field down";
            this.rawDecodeMoveDownButton.Click += new EventHandler(this.RawDecodeMoveDownButton_Click);
            //
            // rawDecodeRestoreDefaultButton
            //
            this.rawDecodeRestoreDefaultButton.Location = new Point(220, 224);
            this.rawDecodeRestoreDefaultButton.Size = new Size(124, 28);
            this.rawDecodeRestoreDefaultButton.TabIndex = 3;
            this.rawDecodeRestoreDefaultButton.Text = "Restore Default";
            this.rawDecodeRestoreDefaultButton.UseVisualStyleBackColor = true;
            this.rawDecodeRestoreDefaultButton.AccessibleName = "Restore default order for Raw Decodes rows";
            this.rawDecodeRestoreDefaultButton.Click += new EventHandler(this.RawDecodeRestoreDefaultButton_Click);
            //
            // rawDecodeTabPage
            //
            this.rawDecodeTabPage.Controls.Add(this.rawDecodeInstructionsLabel);
            this.rawDecodeTabPage.Controls.Add(this.rawDecodeListBox);
            this.rawDecodeTabPage.Controls.Add(this.rawDecodeMoveUpButton);
            this.rawDecodeTabPage.Controls.Add(this.rawDecodeMoveDownButton);
            this.rawDecodeTabPage.Controls.Add(this.rawDecodeRestoreDefaultButton);
            this.rawDecodeTabPage.Text = "Raw Decodes Row";
            this.rawDecodeTabPage.AccessibleName = "Raw Decodes Row";
            this.rawDecodeTabPage.UseVisualStyleBackColor = true;

            //
            // spotWatchInstructionsLabel
            //
            this.spotWatchInstructionsLabel.AutoSize = true;
            this.spotWatchInstructionsLabel.Location = new Point(8, 8);
            this.spotWatchInstructionsLabel.Size = new Size(314, 13);
            this.spotWatchInstructionsLabel.Text = "Checked fields are shown; use Move Up/Move Down to change display order.";
            this.spotWatchInstructionsLabel.AccessibleName = "Spot Watch row editor instructions";
            //
            // spotWatchListBox
            //
            this.spotWatchListBox.FormattingEnabled = true;
            this.spotWatchListBox.Location = new Point(8, 28);
            this.spotWatchListBox.Size = new Size(336, 190);
            this.spotWatchListBox.TabIndex = 0;
            this.spotWatchListBox.CheckOnClick = true;
            this.spotWatchListBox.AccessibleName = "Spot Watch Row Fields List Box";
            this.spotWatchListBox.AccessibleDescription = "List of possible fields for Spot Watch rows. Check fields to show them and uncheck fields to hide them. Select an item and use Move Up or Move Down to reorder.";
            this.spotWatchListBox.SelectedIndexChanged += new EventHandler(this.SpotWatchListBox_SelectedIndexChanged);
            //
            // spotWatchMoveUpButton
            //
            this.spotWatchMoveUpButton.Location = new Point(8, 224);
            this.spotWatchMoveUpButton.Size = new Size(100, 28);
            this.spotWatchMoveUpButton.TabIndex = 1;
            this.spotWatchMoveUpButton.Text = "Move Up";
            this.spotWatchMoveUpButton.UseVisualStyleBackColor = true;
            this.spotWatchMoveUpButton.AccessibleName = "Move selected field up";
            this.spotWatchMoveUpButton.Click += new EventHandler(this.SpotWatchMoveUpButton_Click);
            //
            // spotWatchMoveDownButton
            //
            this.spotWatchMoveDownButton.Location = new Point(114, 224);
            this.spotWatchMoveDownButton.Size = new Size(100, 28);
            this.spotWatchMoveDownButton.TabIndex = 2;
            this.spotWatchMoveDownButton.Text = "Move Down";
            this.spotWatchMoveDownButton.UseVisualStyleBackColor = true;
            this.spotWatchMoveDownButton.AccessibleName = "Move selected field down";
            this.spotWatchMoveDownButton.Click += new EventHandler(this.SpotWatchMoveDownButton_Click);
            //
            // spotWatchRestoreDefaultButton
            //
            this.spotWatchRestoreDefaultButton.Location = new Point(220, 224);
            this.spotWatchRestoreDefaultButton.Size = new Size(124, 28);
            this.spotWatchRestoreDefaultButton.TabIndex = 3;
            this.spotWatchRestoreDefaultButton.Text = "Restore Default";
            this.spotWatchRestoreDefaultButton.UseVisualStyleBackColor = true;
            this.spotWatchRestoreDefaultButton.AccessibleName = "Restore default order for Spot Watch rows";
            this.spotWatchRestoreDefaultButton.Click += new EventHandler(this.SpotWatchRestoreDefaultButton_Click);
            //
            // spotWatchTabPage
            //
            this.spotWatchTabPage.Controls.Add(this.spotWatchInstructionsLabel);
            this.spotWatchTabPage.Controls.Add(this.spotWatchListBox);
            this.spotWatchTabPage.Controls.Add(this.spotWatchMoveUpButton);
            this.spotWatchTabPage.Controls.Add(this.spotWatchMoveDownButton);
            this.spotWatchTabPage.Controls.Add(this.spotWatchRestoreDefaultButton);
            this.spotWatchTabPage.Text = "Spot Watch Row";
            this.spotWatchTabPage.AccessibleName = "Spot Watch Row";
            this.spotWatchTabPage.UseVisualStyleBackColor = true;

            //
            // tabControl
            //
            this.tabControl.Controls.Add(this.callWaitingTabPage);
            this.tabControl.Controls.Add(this.rawDecodeTabPage);
            this.tabControl.Controls.Add(this.spotWatchTabPage);
            this.tabControl.Location = new Point(12, 12);
            this.tabControl.Size = new Size(360, 290);
            this.tabControl.TabIndex = 0;
            this.tabControl.AccessibleName = "";

            //
            // okButton
            //
            this.okButton.Location = new Point(196, 310);
            this.okButton.Size = new Size(88, 28);
            this.okButton.TabIndex = 1;
            this.okButton.Text = "OK";
            this.okButton.UseVisualStyleBackColor = true;
            this.okButton.AccessibleName = "OK";
            this.okButton.AccessibleDescription = "Save the selected order for both tabs and close the editor.";
            this.okButton.Click += new EventHandler(this.OkButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.Location = new Point(290, 310);
            this.cancelButton.Size = new Size(88, 28);
            this.cancelButton.TabIndex = 2;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.AccessibleName = "Cancel";
            this.cancelButton.AccessibleDescription = "Close the editor without saving changes.";
            this.cancelButton.DialogResult = DialogResult.Cancel;
            //
            // RowDisplayOrderDlg
            //
            this.AcceptButton = this.okButton;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new Size(384, 350);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.okButton);
            this.Controls.Add(this.tabControl);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "RowDisplayOrderDlg";
            this.Padding = new Padding(9);
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Row Display Order";
            this.AccessibleName = "Row Display Order";
            this.ShowInTaskbar = false;
            this.ResumeLayout(false);
        }
    }
}
