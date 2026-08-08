
namespace WSJTX_Controller
{
    partial class OptionsDlg
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.logbookSyncTabPage = new System.Windows.Forms.TabPage();
            this.logbookSyncPanel   = new System.Windows.Forms.Panel();
            this.lookupTabPage = new System.Windows.Forms.TabPage();
            this.lookupPanel   = new System.Windows.Forms.Panel();
            this.generalTabPage = new System.Windows.Forms.TabPage();
            this.generalPanel = new System.Windows.Forms.Panel();
            this.receiveReplyTabPage = new System.Windows.Forms.TabPage();
            this.receiveReplyPanel = new System.Windows.Forms.Panel();
            this.rcvCallingGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvReplyingGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvDirectedCqGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvReplyBehaviorGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvBlockListGroupBox = new System.Windows.Forms.GroupBox();
            this.transmitTabPage = new System.Windows.Forms.TabPage();
            this.transmitPanel = new System.Windows.Forms.Panel();
            this.rcvTransmitGroupBox = new System.Windows.Forms.GroupBox();
            this.basicTabPage = new System.Windows.Forms.TabPage();
            this.hotkeysTabPage = new System.Windows.Forms.TabPage();
            this.hotkeysPanel = new System.Windows.Forms.Panel();
            this.advUiTabPage = new System.Windows.Forms.TabPage();
            this.advUiPanel = new System.Windows.Forms.Panel();
            this.wantedCallsTabPage = new System.Windows.Forms.TabPage();
            this.wantedCallsPanel = new System.Windows.Forms.Panel();
            this.spotWatchTabPage = new System.Windows.Forms.TabPage();
            this.spotWatchPanel = new System.Windows.Forms.Panel();
            this.radioTabPage = new System.Windows.Forms.TabPage();
            this.radioPanel = new System.Windows.Forms.Panel();
            this.decodeEngineTabPage = new System.Windows.Forms.TabPage();
            this.decodeEnginePanel = new System.Windows.Forms.Panel();
            this.soundsTabPage = new System.Windows.Forms.TabPage();
            this.soundsPanel = new System.Windows.Forms.Panel();
            this.appearanceTabPage = new System.Windows.Forms.TabPage();
            this.appearancePanel = new System.Windows.Forms.Panel();
            this.udpTabPage = new System.Windows.Forms.TabPage();
            this.udpPanel = new System.Windows.Forms.Panel();
            this.udpGroupBox = new System.Windows.Forms.GroupBox();
            this.udpOverrideCheckBox = new System.Windows.Forms.CheckBox();
            this.udpHelpButton = new System.Windows.Forms.Button();
            this.udpAddrStaticLabel = new System.Windows.Forms.Label();
            this.udpAddrTextBox = new System.Windows.Forms.TextBox();
            this.udpAddrLabel = new System.Windows.Forms.Label();
            this.udpPortStaticLabel = new System.Windows.Forms.Label();
            this.udpPortTextBox = new System.Windows.Forms.TextBox();
            this.udpPortStdLabel = new System.Windows.Forms.Label();
            this.udpMulticastCheckBox = new System.Windows.Forms.CheckBox();
            this.udpOnTopCheckBox = new System.Windows.Forms.CheckBox();
            this.udpDiagLogCheckBox = new System.Windows.Forms.CheckBox();
            this.filterGroupBox = new System.Windows.Forms.GroupBox();
            this.subtitleLabel = new System.Windows.Forms.TextBox();
            this.modeLabel = new System.Windows.Forms.TextBox();
            this.callCqButton = new System.Windows.Forms.CheckBox();
            this.listenButton = new System.Windows.Forms.CheckBox();
            this.label12 = new System.Windows.Forms.TextBox();
            this.cqButton = new System.Windows.Forms.CheckBox();
            this.cqDxButton = new System.Windows.Forms.CheckBox();
            this.label2 = new System.Windows.Forms.TextBox();
            this.dxButton = new System.Windows.Forms.CheckBox();
            this.nonDxButton = new System.Windows.Forms.CheckBox();
            this.label4 = new System.Windows.Forms.TextBox();
            this.potaButton = new System.Windows.Forms.CheckBox();
            this.hunterButton = new System.Windows.Forms.CheckBox();
            this.label5 = new System.Windows.Forms.TextBox();
            this.allButton = new System.Windows.Forms.CheckBox();
            this.recentButton = new System.Windows.Forms.CheckBox();
            this.label9 = new System.Windows.Forms.TextBox();
            this.okButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.lookupTabPage.SuspendLayout();
            this.tabControl1.SuspendLayout();
            this.generalTabPage.SuspendLayout();
            this.generalPanel.SuspendLayout();
            this.receiveReplyTabPage.SuspendLayout();
            this.receiveReplyPanel.SuspendLayout();
            this.transmitTabPage.SuspendLayout();
            this.transmitPanel.SuspendLayout();
            this.basicTabPage.SuspendLayout();
            this.hotkeysTabPage.SuspendLayout();
            this.advUiTabPage.SuspendLayout();
            this.advUiPanel.SuspendLayout();
            this.wantedCallsTabPage.SuspendLayout();
            this.wantedCallsPanel.SuspendLayout();
            this.spotWatchTabPage.SuspendLayout();
            this.spotWatchPanel.SuspendLayout();
            this.radioTabPage.SuspendLayout();
            this.radioPanel.SuspendLayout();
            this.decodeEngineTabPage.SuspendLayout();
            this.decodeEnginePanel.SuspendLayout();
            this.soundsTabPage.SuspendLayout();
            this.soundsPanel.SuspendLayout();
            this.appearanceTabPage.SuspendLayout();
            this.appearancePanel.SuspendLayout();
            this.udpTabPage.SuspendLayout();
            this.udpPanel.SuspendLayout();
            this.udpGroupBox.SuspendLayout();
            this.SuspendLayout();
            //
            // tabControl1
            //
            this.tabControl1.Controls.Add(this.basicTabPage);          // index 0
            this.tabControl1.Controls.Add(this.generalTabPage);       // index 1
            this.tabControl1.Controls.Add(this.receiveReplyTabPage);  // index 2
            this.tabControl1.Controls.Add(this.transmitTabPage);      // index 3
            this.tabControl1.Controls.Add(this.hotkeysTabPage);       // index 4
            this.tabControl1.Controls.Add(this.advUiTabPage);         // index 5
            this.tabControl1.Controls.Add(this.wantedCallsTabPage);   // index 6
            this.tabControl1.Controls.Add(this.spotWatchTabPage);     // index 7
            this.tabControl1.Controls.Add(this.soundsTabPage);        // index 8
            this.tabControl1.Controls.Add(this.udpTabPage);           // index 9
            this.tabControl1.Controls.Add(this.radioTabPage);         // index 10
            this.tabControl1.Controls.Add(this.decodeEngineTabPage);  // index 11
            this.tabControl1.Controls.Add(this.logbookSyncTabPage);   // index 12
            this.tabControl1.Controls.Add(this.lookupTabPage);        // index 13
            this.tabControl1.Controls.Add(this.appearanceTabPage);    // index 14
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Multiline = true;
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(830, 380);
            this.tabControl1.TabIndex = 0;
            this.tabControl1.AccessibleName = "";
            //
            // generalTabPage
            //
            this.generalTabPage.Controls.Add(this.generalPanel);
            this.generalTabPage.Text = "General";
            this.generalTabPage.Name = "generalTabPage";
            this.generalTabPage.AccessibleName = "General tab";
            //
            // generalPanel
            //
            this.generalPanel.Controls.Add(this.udpOnTopCheckBox);
            this.generalPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.generalPanel.Name = "generalPanel";
            //
            // receiveReplyTabPage
            //
            this.receiveReplyTabPage.Controls.Add(this.receiveReplyPanel);
            this.receiveReplyTabPage.Text = "Receive / Auto Reply";
            this.receiveReplyTabPage.Name = "receiveReplyTabPage";
            this.receiveReplyTabPage.AccessibleName = "Receive Auto Reply tab";
            //
            // receiveReplyPanel
            //
            this.receiveReplyPanel.AutoScroll = true;
            this.receiveReplyPanel.Controls.Add(this.rcvCallingGroupBox);
            this.receiveReplyPanel.Controls.Add(this.rcvReplyingGroupBox);
            this.receiveReplyPanel.Controls.Add(this.rcvDirectedCqGroupBox);
            this.receiveReplyPanel.Controls.Add(this.rcvReplyBehaviorGroupBox);
            this.receiveReplyPanel.Controls.Add(this.rcvBlockListGroupBox);
            this.receiveReplyPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.receiveReplyPanel.Name = "receiveReplyPanel";
            //
            // rcvCallingGroupBox
            //
            this.rcvCallingGroupBox.AccessibleName = "Calling options";
            this.rcvCallingGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.rcvCallingGroupBox.Location = new System.Drawing.Point(5, 5);
            this.rcvCallingGroupBox.Name = "rcvCallingGroupBox";
            this.rcvCallingGroupBox.Size = new System.Drawing.Size(650, 115);
            this.rcvCallingGroupBox.TabStop = false;
            this.rcvCallingGroupBox.Text = "Calling";
            //
            // rcvReplyingGroupBox
            //
            this.rcvReplyingGroupBox.AccessibleName = "Replying options";
            this.rcvReplyingGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.rcvReplyingGroupBox.Location = new System.Drawing.Point(5, 125);
            this.rcvReplyingGroupBox.Name = "rcvReplyingGroupBox";
            this.rcvReplyingGroupBox.Size = new System.Drawing.Size(650, 105);
            this.rcvReplyingGroupBox.TabStop = false;
            this.rcvReplyingGroupBox.Text = "Replying";
            //
            // rcvDirectedCqGroupBox
            //
            this.rcvDirectedCqGroupBox.AccessibleName = "Directed CQ Alert options";
            this.rcvDirectedCqGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.rcvDirectedCqGroupBox.Location = new System.Drawing.Point(5, 235);
            this.rcvDirectedCqGroupBox.Name = "rcvDirectedCqGroupBox";
            this.rcvDirectedCqGroupBox.Size = new System.Drawing.Size(650, 55);
            this.rcvDirectedCqGroupBox.TabStop = false;
            this.rcvDirectedCqGroupBox.Text = "Directed CQ Alert";
            //
            // rcvReplyBehaviorGroupBox
            //
            this.rcvReplyBehaviorGroupBox.AccessibleName = "Reply Behavior options";
            this.rcvReplyBehaviorGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.rcvReplyBehaviorGroupBox.Location = new System.Drawing.Point(5, 370);
            this.rcvReplyBehaviorGroupBox.Name = "rcvReplyBehaviorGroupBox";
            this.rcvReplyBehaviorGroupBox.Size = new System.Drawing.Size(650, 50);
            this.rcvReplyBehaviorGroupBox.TabStop = false;
            this.rcvReplyBehaviorGroupBox.Text = "Reply Behavior";
            //
            // rcvBlockListGroupBox
            //
            this.rcvBlockListGroupBox.AccessibleName = "Block List";
            this.rcvBlockListGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.rcvBlockListGroupBox.Location = new System.Drawing.Point(5, 425);
            this.rcvBlockListGroupBox.Name = "rcvBlockListGroupBox";
            this.rcvBlockListGroupBox.Size = new System.Drawing.Size(650, 74);
            this.rcvBlockListGroupBox.TabStop = false;
            this.rcvBlockListGroupBox.Text = "Block List";
            //
            // transmitTabPage
            //
            this.transmitTabPage.Controls.Add(this.transmitPanel);
            this.transmitTabPage.Text = "Transmit";
            this.transmitTabPage.Name = "transmitTabPage";
            this.transmitTabPage.AccessibleName = "Transmit tab";
            //
            // transmitPanel
            //
            this.transmitPanel.Controls.Add(this.rcvTransmitGroupBox);
            this.transmitPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.transmitPanel.Name = "transmitPanel";
            //
            // rcvTransmitGroupBox
            //
            this.rcvTransmitGroupBox.AccessibleName = "Transmit options";
            this.rcvTransmitGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.rcvTransmitGroupBox.Location = new System.Drawing.Point(5, 5);
            this.rcvTransmitGroupBox.Name = "rcvTransmitGroupBox";
            this.rcvTransmitGroupBox.Size = new System.Drawing.Size(650, 165);
            this.rcvTransmitGroupBox.TabStop = false;
            this.rcvTransmitGroupBox.Text = "Transmit";
            //
            // basicTabPage
            //
            this.basicTabPage.Controls.Add(this.subtitleLabel);
            this.basicTabPage.Controls.Add(this.modeLabel);
            this.basicTabPage.Controls.Add(this.callCqButton);
            this.basicTabPage.Controls.Add(this.listenButton);
            this.basicTabPage.Controls.Add(this.label12);
            this.basicTabPage.Controls.Add(this.cqButton);
            this.basicTabPage.Controls.Add(this.cqDxButton);
            this.basicTabPage.Controls.Add(this.label2);
            this.basicTabPage.Controls.Add(this.dxButton);
            this.basicTabPage.Controls.Add(this.nonDxButton);
            this.basicTabPage.Controls.Add(this.label4);
            this.basicTabPage.Controls.Add(this.potaButton);
            this.basicTabPage.Controls.Add(this.hunterButton);
            this.basicTabPage.Controls.Add(this.label5);
            this.basicTabPage.Controls.Add(this.allButton);
            this.basicTabPage.Controls.Add(this.recentButton);
            this.basicTabPage.Controls.Add(this.filterGroupBox);
            this.basicTabPage.Controls.Add(this.label9);
            this.basicTabPage.Text = "Basic";
            this.basicTabPage.Name = "basicTabPage";
            //
            // hotkeysTabPage
            //
            this.hotkeysTabPage.Controls.Add(this.hotkeysPanel);
            this.hotkeysTabPage.Text = "Hotkeys";
            this.hotkeysTabPage.Name = "hotkeysTabPage";
            this.hotkeysTabPage.AccessibleName = "Hotkeys tab";
            //
            // hotkeysPanel
            //
            this.hotkeysPanel.AutoScroll = true;
            this.hotkeysPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.hotkeysPanel.Name = "hotkeysPanel";
            //
            // advancedTabPage
            //
            // advUiTabPage
            //
            this.advUiTabPage.Controls.Add(this.advUiPanel);
            this.advUiTabPage.Text = "Advanced UI";
            this.advUiTabPage.Name = "advUiTabPage";
            this.advUiTabPage.AccessibleName = "Advanced UI tab";
            //
            // advUiPanel
            //
            this.advUiPanel.AutoScroll = true;
            this.advUiPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.advUiPanel.Name = "advUiPanel";
            //
            // wantedCallsTabPage
            //
            this.wantedCallsTabPage.Controls.Add(this.wantedCallsPanel);
            this.wantedCallsTabPage.Text = "Wanted Calls";
            this.wantedCallsTabPage.Name = "wantedCallsTabPage";
            this.wantedCallsTabPage.AccessibleName = "Wanted Calls tab";
            //
            // wantedCallsPanel
            //
            this.wantedCallsPanel.AutoScroll = true;
            this.wantedCallsPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.wantedCallsPanel.Name = "wantedCallsPanel";
            //
            // spotWatchTabPage
            //
            this.spotWatchTabPage.Controls.Add(this.spotWatchPanel);
            this.spotWatchTabPage.Text = "Spot Watch";
            this.spotWatchTabPage.Name = "spotWatchTabPage";
            this.spotWatchTabPage.AccessibleName = "Spot Watch tab";
            //
            // spotWatchPanel
            //
            this.spotWatchPanel.AutoScroll = true;
            this.spotWatchPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.spotWatchPanel.Name = "spotWatchPanel";
            //
            // radioTabPage
            //
            this.radioTabPage.Controls.Add(this.radioPanel);
            this.radioTabPage.Text = "Radio";
            this.radioTabPage.Name = "radioTabPage";
            this.radioTabPage.AccessibleName = "Radio tab";
            //
            // radioPanel
            //
            this.radioPanel.AutoScroll = true;
            this.radioPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.radioPanel.Name = "radioPanel";
            //
            // decodeEngineTabPage
            //
            // Split out from radioTabPage: the Decode Engine section (Jimmy Native / My Call /
            // My Grid / audio device pickers) had grown tall enough to render below the Radio
            // tab's visible area -- AutoScroll being set did not make it reachable by Tab with a
            // real screen reader (confirmed live with JAWS/NVDA, 2026-08-06), so it now gets its
            // own tab with plenty of room instead of relying on scrolling within Radio's tab.
            this.decodeEngineTabPage.Controls.Add(this.decodeEnginePanel);
            this.decodeEngineTabPage.Text = "Decode Engine";
            this.decodeEngineTabPage.Name = "decodeEngineTabPage";
            this.decodeEngineTabPage.AccessibleName = "Decode Engine tab";
            //
            // decodeEnginePanel
            //
            this.decodeEnginePanel.AutoScroll = true;
            this.decodeEnginePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.decodeEnginePanel.Name = "decodeEnginePanel";
            //
            // soundsTabPage
            //
            this.soundsTabPage.Controls.Add(this.soundsPanel);
            this.soundsTabPage.Text = "Sounds";
            this.soundsTabPage.Name = "soundsTabPage";
            this.soundsTabPage.AccessibleName = "Sounds tab";
            //
            // soundsPanel
            //
            this.soundsPanel.AutoScroll = true;
            this.soundsPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.soundsPanel.Name = "soundsPanel";
            //
            // appearanceTabPage
            //
            this.appearanceTabPage.Controls.Add(this.appearancePanel);
            this.appearanceTabPage.Text = "Appearance";
            this.appearanceTabPage.Name = "appearanceTabPage";
            this.appearanceTabPage.AccessibleName = "Appearance tab";
            //
            // appearancePanel
            //
            this.appearancePanel.AutoScroll = true;
            this.appearancePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.appearancePanel.Name = "appearancePanel";
            //
            // logbookSyncTabPage
            //
            this.logbookSyncTabPage.Controls.Add(this.logbookSyncPanel);
            this.logbookSyncTabPage.Text           = "Logbook Sync";
            this.logbookSyncTabPage.Name           = "logbookSyncTabPage";
            this.logbookSyncTabPage.AccessibleName = "Logbook Sync tab";
            //
            // logbookSyncPanel
            //
            this.logbookSyncPanel.AutoScroll = true;
            this.logbookSyncPanel.Dock       = System.Windows.Forms.DockStyle.Fill;
            this.logbookSyncPanel.Name       = "logbookSyncPanel";
            //
            // lookupTabPage
            //
            this.lookupTabPage.Controls.Add(this.lookupPanel);
            this.lookupTabPage.Text           = "Lookup Data";
            this.lookupTabPage.Name           = "lookupTabPage";
            this.lookupTabPage.AccessibleName = "Lookup Data tab";
            //
            // lookupPanel
            //
            this.lookupPanel.AutoScroll = true;
            this.lookupPanel.Dock       = System.Windows.Forms.DockStyle.Fill;
            this.lookupPanel.Name       = "lookupPanel";
            //
            // udpTabPage
            //
            this.udpTabPage.Controls.Add(this.udpPanel);
            this.udpTabPage.Text = "UDP / Connection";
            this.udpTabPage.Name = "udpTabPage";
            //
            // udpPanel
            //
            this.udpPanel.Controls.Add(this.udpGroupBox);
            this.udpPanel.Controls.Add(this.udpDiagLogCheckBox);
            this.udpPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.udpPanel.Name = "udpPanel";
            //
            // udpGroupBox
            //
            this.udpGroupBox.AccessibleName = "UDP server settings group";
            this.udpGroupBox.Controls.Add(this.udpOverrideCheckBox);
            this.udpGroupBox.Controls.Add(this.udpHelpButton);
            this.udpGroupBox.Controls.Add(this.udpAddrStaticLabel);
            this.udpGroupBox.Controls.Add(this.udpAddrTextBox);
            this.udpGroupBox.Controls.Add(this.udpAddrLabel);
            this.udpGroupBox.Controls.Add(this.udpPortStaticLabel);
            this.udpGroupBox.Controls.Add(this.udpPortTextBox);
            this.udpGroupBox.Controls.Add(this.udpPortStdLabel);
            this.udpGroupBox.Controls.Add(this.udpMulticastCheckBox);
            this.udpGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.udpGroupBox.Location = new System.Drawing.Point(10, 8);
            this.udpGroupBox.Name = "udpGroupBox";
            this.udpGroupBox.Size = new System.Drawing.Size(640, 155);
            this.udpGroupBox.TabStop = false;
            this.udpGroupBox.Text = "UDP server settings";
            //
            // udpOverrideCheckBox
            //
            this.udpOverrideCheckBox.AccessibleName = "Override UDP automatic detection";
            this.udpOverrideCheckBox.AutoSize = true;
            this.udpOverrideCheckBox.Location = new System.Drawing.Point(12, 22);
            this.udpOverrideCheckBox.Name = "udpOverrideCheckBox";
            this.udpOverrideCheckBox.TabIndex = 0;
            this.udpOverrideCheckBox.Text = "Override automatic detection (not recommended!)";
            this.udpOverrideCheckBox.UseVisualStyleBackColor = true;
            this.udpOverrideCheckBox.CheckedChanged += new System.EventHandler(this.udpOverrideCheckBox_CheckedChanged);
            //
            // udpHelpButton
            //
            this.udpHelpButton.AccessibleName = "UDP connection help";
            this.udpHelpButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, ((System.Drawing.FontStyle)((System.Drawing.FontStyle.Bold | System.Drawing.FontStyle.Italic))), System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.udpHelpButton.ForeColor = System.Drawing.Color.Blue;
            this.udpHelpButton.Location = new System.Drawing.Point(294, 0);
            this.udpHelpButton.Name = "udpHelpButton";
            this.udpHelpButton.Size = new System.Drawing.Size(26, 22);
            this.udpHelpButton.TabIndex = 4;
            this.udpHelpButton.Text = "?";
            this.udpHelpButton.UseVisualStyleBackColor = true;
            this.udpHelpButton.Click += new System.EventHandler(this.udpHelpButton_Click);
            //
            // udpAddrStaticLabel
            //
            this.udpAddrStaticLabel.AutoSize = true;
            this.udpAddrStaticLabel.Location = new System.Drawing.Point(28, 52);
            this.udpAddrStaticLabel.Name = "udpAddrStaticLabel";
            this.udpAddrStaticLabel.Text = "UDP address:";
            //
            // udpAddrTextBox
            //
            this.udpAddrTextBox.AccessibleName = "UDP address text box";
            this.udpAddrTextBox.Location = new System.Drawing.Point(130, 49);
            this.udpAddrTextBox.Name = "udpAddrTextBox";
            this.udpAddrTextBox.Size = new System.Drawing.Size(120, 20);
            this.udpAddrTextBox.TabIndex = 1;
            //
            // udpAddrLabel
            //
            this.udpAddrLabel.AutoSize = true;
            this.udpAddrLabel.Location = new System.Drawing.Point(258, 52);
            this.udpAddrLabel.Name = "udpAddrLabel";
            this.udpAddrLabel.Text = "(Standard: 239.255.0.0)";
            //
            // udpPortStaticLabel
            //
            this.udpPortStaticLabel.AutoSize = true;
            this.udpPortStaticLabel.Location = new System.Drawing.Point(28, 79);
            this.udpPortStaticLabel.Name = "udpPortStaticLabel";
            this.udpPortStaticLabel.Text = "UDP port:";
            //
            // udpPortTextBox
            //
            this.udpPortTextBox.AccessibleName = "UDP port text box";
            this.udpPortTextBox.Location = new System.Drawing.Point(130, 76);
            this.udpPortTextBox.Name = "udpPortTextBox";
            this.udpPortTextBox.Size = new System.Drawing.Size(120, 20);
            this.udpPortTextBox.TabIndex = 2;
            //
            // udpPortStdLabel
            //
            this.udpPortStdLabel.AutoSize = true;
            this.udpPortStdLabel.Location = new System.Drawing.Point(258, 79);
            this.udpPortStdLabel.Name = "udpPortStdLabel";
            this.udpPortStdLabel.Text = "(Standard: 2237)";
            //
            // udpMulticastCheckBox
            //
            this.udpMulticastCheckBox.AccessibleName = "Multicast";
            this.udpMulticastCheckBox.AutoSize = true;
            this.udpMulticastCheckBox.Location = new System.Drawing.Point(28, 107);
            this.udpMulticastCheckBox.Name = "udpMulticastCheckBox";
            this.udpMulticastCheckBox.TabIndex = 3;
            this.udpMulticastCheckBox.Text = "Multicast (also select an \"Outgoing interface\" in WSJT-X)";
            this.udpMulticastCheckBox.UseVisualStyleBackColor = true;
            this.udpMulticastCheckBox.CheckedChanged += new System.EventHandler(this.udpMulticastCheckBox_CheckedChanged);
            //
            // udpOnTopCheckBox  (moved to General tab / generalPanel)
            //
            this.udpOnTopCheckBox.AccessibleName = "Always on top";
            this.udpOnTopCheckBox.AutoSize = true;
            this.udpOnTopCheckBox.Location = new System.Drawing.Point(10, 15);
            this.udpOnTopCheckBox.Name = "udpOnTopCheckBox";
            this.udpOnTopCheckBox.TabIndex = 0;
            this.udpOnTopCheckBox.Text = "Always on top";
            this.udpOnTopCheckBox.UseVisualStyleBackColor = true;
            //
            // udpDiagLogCheckBox
            //
            this.udpDiagLogCheckBox.AccessibleName = "Log diagnostic info";
            this.udpDiagLogCheckBox.AutoSize = true;
            this.udpDiagLogCheckBox.Location = new System.Drawing.Point(10, 170);
            this.udpDiagLogCheckBox.Name = "udpDiagLogCheckBox";
            this.udpDiagLogCheckBox.TabIndex = 4;
            this.udpDiagLogCheckBox.Text = "Log diagnostic info";
            this.udpDiagLogCheckBox.UseVisualStyleBackColor = true;
            //
            // filterGroupBox  (remains on Basic tab; receives no reparented controls after Phase 2)
            //
            this.filterGroupBox.AccessibleName = "Reply to new calls filter";
            this.filterGroupBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this.filterGroupBox.Location = new System.Drawing.Point(5, 218);
            this.filterGroupBox.Name = "filterGroupBox";
            this.filterGroupBox.Size = new System.Drawing.Size(655, 70);
            this.filterGroupBox.TabStop = false;
            this.filterGroupBox.Text = "Reply to new calls";
            //
            // subtitleLabel
            //
            this.subtitleLabel.AccessibleDescription = "";
            this.subtitleLabel.AccessibleName = "Setup wizard instructions";
            this.subtitleLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
            this.subtitleLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Bold);
            this.subtitleLabel.Location = new System.Drawing.Point(10, 10);
            this.subtitleLabel.Name = "subtitleLabel";
            this.subtitleLabel.ReadOnly = true;
            this.subtitleLabel.Size = new System.Drawing.Size(650, 22);
            this.subtitleLabel.TabIndex = 0;
            this.subtitleLabel.Text = "Use this dialog to set operating options. Tab through the rows and press a button.";
            this.subtitleLabel.Enter += new System.EventHandler(this.subtitleLabel_Enter);
            //
            // modeLabel
            //
            this.modeLabel.AccessibleName = "Operating mode question";
            this.modeLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
            this.modeLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.modeLabel.Location = new System.Drawing.Point(10, 40);
            this.modeLabel.Name = "modeLabel";
            this.modeLabel.ReadOnly = true;
            this.modeLabel.Size = new System.Drawing.Size(440, 22);
            this.modeLabel.TabIndex = 1;
            this.modeLabel.Text = "Do you want to call CQ, or only listen for interesting calls?";
            this.modeLabel.Enter += new System.EventHandler(this.modeLabel_Enter);
            //
            // callCqButton
            //
            this.callCqButton.AccessibleName = "Call CQ mode";
            this.callCqButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.callCqButton.Location = new System.Drawing.Point(460, 38);
            this.callCqButton.Name = "callCqButton";
            this.callCqButton.Size = new System.Drawing.Size(100, 27);
            this.callCqButton.TabIndex = 2;
            this.callCqButton.Text = "'Call CQ' mode";
            this.callCqButton.UseVisualStyleBackColor = true;
            this.callCqButton.Click += new System.EventHandler(this.callCqButton_Click);
            //
            // listenButton
            //
            this.listenButton.AccessibleName = "Listen mode";
            this.listenButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.listenButton.Location = new System.Drawing.Point(565, 38);
            this.listenButton.Name = "listenButton";
            this.listenButton.Size = new System.Drawing.Size(105, 27);
            this.listenButton.TabIndex = 3;
            this.listenButton.Text = "'Listen' mode";
            this.listenButton.UseVisualStyleBackColor = true;
            this.listenButton.Click += new System.EventHandler(this.listenButton_Click);
            //
            // label12
            //
            this.label12.AccessibleName = "CQ type question";
            this.label12.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
            this.label12.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.label12.Location = new System.Drawing.Point(10, 76);
            this.label12.Name = "label12";
            this.label12.ReadOnly = true;
            this.label12.Size = new System.Drawing.Size(440, 22);
            this.label12.TabIndex = 4;
            this.label12.Text = "When calling CQ, call CQ or CQ DX? (You can choose one or both)";
            this.label12.Enter += new System.EventHandler(this.label12_Enter);
            //
            // cqButton
            //
            this.cqButton.AccessibleName = "CQ";
            this.cqButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.cqButton.Location = new System.Drawing.Point(460, 74);
            this.cqButton.Name = "cqButton";
            this.cqButton.Size = new System.Drawing.Size(100, 27);
            this.cqButton.TabIndex = 5;
            this.cqButton.Text = "CQ";
            this.cqButton.UseVisualStyleBackColor = true;
            this.cqButton.Click += new System.EventHandler(this.cqButton_Click);
            //
            // cqDxButton
            //
            this.cqDxButton.AccessibleName = "CQ DX";
            this.cqDxButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.cqDxButton.Location = new System.Drawing.Point(565, 74);
            this.cqDxButton.Name = "cqDxButton";
            this.cqDxButton.Size = new System.Drawing.Size(105, 27);
            this.cqDxButton.TabIndex = 6;
            this.cqDxButton.Text = "CQ DX";
            this.cqDxButton.UseVisualStyleBackColor = true;
            this.cqDxButton.Click += new System.EventHandler(this.cqDxButton_Click);
            //
            // label2
            //
            this.label2.AccessibleName = "Reply target question";
            this.label2.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
            this.label2.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.label2.Location = new System.Drawing.Point(10, 112);
            this.label2.Name = "label2";
            this.label2.ReadOnly = true;
            this.label2.Size = new System.Drawing.Size(440, 22);
            this.label2.TabIndex = 7;
            this.label2.Text = "In addition to calls to you, which calls do you want to reply to?";
            this.label2.Enter += new System.EventHandler(this.label2_Enter);
            //
            // dxButton
            //
            this.dxButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.dxButton.Location = new System.Drawing.Point(460, 110);
            this.dxButton.Name = "dxButton";
            this.dxButton.Size = new System.Drawing.Size(100, 27);
            this.dxButton.TabIndex = 8;
            this.dxButton.Text = "Reply to DX";
            this.dxButton.UseVisualStyleBackColor = true;
            this.dxButton.Click += new System.EventHandler(this.dxButton_Click);
            //
            // nonDxButton
            //
            this.nonDxButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9F);
            this.nonDxButton.Location = new System.Drawing.Point(565, 110);
            this.nonDxButton.Name = "nonDxButton";
            this.nonDxButton.Size = new System.Drawing.Size(105, 27);
            this.nonDxButton.TabIndex = 9;
            this.nonDxButton.Text = "Reply to my continent";
            this.nonDxButton.UseVisualStyleBackColor = true;
            this.nonDxButton.Click += new System.EventHandler(this.nonDxButton_Click);
            //
            // label4
            //
            this.label4.AccessibleName = "POTA role question";
            this.label4.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
            this.label4.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.label4.Location = new System.Drawing.Point(10, 148);
            this.label4.Name = "label4";
            this.label4.ReadOnly = true;
            this.label4.Size = new System.Drawing.Size(440, 22);
            this.label4.TabIndex = 10;
            this.label4.Text = "If you're operating Parks on the Air, what will you be doing?";
            this.label4.Enter += new System.EventHandler(this.label4_Enter);
            //
            // potaButton
            //
            this.potaButton.AccessibleName = "POTA Activator";
            this.potaButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.potaButton.Location = new System.Drawing.Point(460, 146);
            this.potaButton.Name = "potaButton";
            this.potaButton.Size = new System.Drawing.Size(100, 27);
            this.potaButton.TabIndex = 11;
            this.potaButton.Text = "Activator";
            this.potaButton.UseVisualStyleBackColor = true;
            this.potaButton.Click += new System.EventHandler(this.potaButton_Click);
            //
            // hunterButton
            //
            this.hunterButton.AccessibleName = "POTA Hunter";
            this.hunterButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.hunterButton.Location = new System.Drawing.Point(565, 146);
            this.hunterButton.Name = "hunterButton";
            this.hunterButton.Size = new System.Drawing.Size(105, 27);
            this.hunterButton.TabIndex = 12;
            this.hunterButton.Text = "Hunter";
            this.hunterButton.UseVisualStyleBackColor = true;
            this.hunterButton.Click += new System.EventHandler(this.hunterButton_Click);
            //
            // label5
            //
            this.label5.AccessibleName = "Reply order question";
            this.label5.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
            this.label5.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.label5.Location = new System.Drawing.Point(10, 184);
            this.label5.Name = "label5";
            this.label5.ReadOnly = true;
            this.label5.Size = new System.Drawing.Size(440, 22);
            this.label5.TabIndex = 13;
            this.label5.Text = "When replying to calls, reply in order received, or to most-recent first?";
            this.label5.Enter += new System.EventHandler(this.label5_Enter);
            //
            // allButton
            //
            this.allButton.AccessibleName = "reply in call order";
            this.allButton.AccessibleRole = System.Windows.Forms.AccessibleRole.RadioButton;
            this.allButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.allButton.Location = new System.Drawing.Point(460, 182);
            this.allButton.Name = "allButton";
            this.allButton.Size = new System.Drawing.Size(100, 27);
            this.allButton.TabIndex = 14;
            this.allButton.Text = "Reply in order";
            this.allButton.UseVisualStyleBackColor = true;
            this.allButton.Click += new System.EventHandler(this.allButton_Click);
            //
            // recentButton
            //
            this.recentButton.AccessibleName = "Reply to most recent first";
            this.recentButton.AccessibleRole = System.Windows.Forms.AccessibleRole.RadioButton;
            this.recentButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F);
            this.recentButton.Location = new System.Drawing.Point(565, 182);
            this.recentButton.Name = "recentButton";
            this.recentButton.Size = new System.Drawing.Size(105, 27);
            this.recentButton.TabIndex = 15;
            this.recentButton.Text = "Reply to recent first";
            this.recentButton.UseVisualStyleBackColor = true;
            this.recentButton.Click += new System.EventHandler(this.recentButton_Click);
            //
            // label9
            //
            this.label9.AccessibleName = "Ready to start message";
            this.label9.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
            this.label9.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Bold);
            this.label9.Location = new System.Drawing.Point(10, 326);
            this.label9.Name = "label9";
            this.label9.ReadOnly = true;
            this.label9.Size = new System.Drawing.Size(540, 22);
            this.label9.TabIndex = 16;
            this.label9.Text = "You're now ready to start. Press OK to close this Options dialog.";
            this.label9.Enter += new System.EventHandler(this.label9_Enter);
            //
            // okButton
            //
            this.okButton.AccessibleName = "OK, confirm changes";
            this.okButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Bold);
            this.okButton.Location = new System.Drawing.Point(618, 386);
            this.okButton.Name = "okButton";
            this.okButton.Size = new System.Drawing.Size(100, 27);
            this.okButton.TabIndex = 1;
            this.okButton.Text = "OK";
            this.okButton.UseVisualStyleBackColor = true;
            this.okButton.Click += new System.EventHandler(this.okButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.AccessibleName = "Cancel, discard changes";
            this.cancelButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Bold);
            this.cancelButton.Location = new System.Drawing.Point(723, 386);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(100, 27);
            this.cancelButton.TabIndex = 2;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            //
            // OptionsDlg
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(830, 418);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.okButton);
            this.Controls.Add(this.cancelButton);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.KeyPreview = true;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "OptionsDlg";
            this.Text = "Jimmy Options";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.OptionsDlg_FormClosing);
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.OptionsDlg_FormClosed);
            this.Load += new System.EventHandler(this.OptionsDlg_Load);
            this.KeyDown += new System.Windows.Forms.KeyEventHandler(this.OptionsDlg_KeyDown);
            this.tabControl1.ResumeLayout(false);
            this.generalTabPage.ResumeLayout(false);
            this.generalPanel.ResumeLayout(false);
            this.generalPanel.PerformLayout();
            this.receiveReplyTabPage.ResumeLayout(false);
            this.receiveReplyPanel.ResumeLayout(false);
            this.transmitTabPage.ResumeLayout(false);
            this.transmitPanel.ResumeLayout(false);
            this.basicTabPage.ResumeLayout(false);
            this.basicTabPage.PerformLayout();
            this.hotkeysTabPage.ResumeLayout(false);
            this.advUiTabPage.ResumeLayout(false);
            this.advUiPanel.ResumeLayout(false);
            this.wantedCallsTabPage.ResumeLayout(false);
            this.wantedCallsPanel.ResumeLayout(false);
            this.spotWatchTabPage.ResumeLayout(false);
            this.spotWatchPanel.ResumeLayout(false);
            this.radioTabPage.ResumeLayout(false);
            this.radioPanel.ResumeLayout(false);
            this.decodeEngineTabPage.ResumeLayout(false);
            this.decodeEnginePanel.ResumeLayout(false);
            this.soundsTabPage.ResumeLayout(false);
            this.soundsPanel.ResumeLayout(false);
            this.appearanceTabPage.ResumeLayout(false);
            this.appearancePanel.ResumeLayout(false);
            this.udpTabPage.ResumeLayout(false);
            this.udpPanel.ResumeLayout(false);
            this.udpPanel.PerformLayout();
            this.udpGroupBox.ResumeLayout(false);
            this.udpGroupBox.PerformLayout();
            this.lookupTabPage.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage generalTabPage;
        private System.Windows.Forms.Panel generalPanel;
        private System.Windows.Forms.TabPage receiveReplyTabPage;
        private System.Windows.Forms.Panel receiveReplyPanel;
        public System.Windows.Forms.GroupBox rcvCallingGroupBox;
        public System.Windows.Forms.GroupBox rcvReplyingGroupBox;
        public System.Windows.Forms.GroupBox rcvDirectedCqGroupBox;
        public System.Windows.Forms.GroupBox rcvReplyBehaviorGroupBox;
        public System.Windows.Forms.GroupBox rcvBlockListGroupBox;
        private System.Windows.Forms.TabPage transmitTabPage;
        private System.Windows.Forms.Panel transmitPanel;
        public System.Windows.Forms.GroupBox rcvTransmitGroupBox;
        private System.Windows.Forms.TabPage basicTabPage;
        private System.Windows.Forms.TabPage hotkeysTabPage;
        private System.Windows.Forms.Panel hotkeysPanel;
        private System.Windows.Forms.TabPage udpTabPage;
        private System.Windows.Forms.Panel udpPanel;
        private System.Windows.Forms.GroupBox udpGroupBox;
        private System.Windows.Forms.CheckBox udpOverrideCheckBox;
        private System.Windows.Forms.Button udpHelpButton;
        private System.Windows.Forms.Label udpAddrStaticLabel;
        private System.Windows.Forms.TextBox udpAddrTextBox;
        private System.Windows.Forms.Label udpAddrLabel;
        private System.Windows.Forms.Label udpPortStaticLabel;
        private System.Windows.Forms.TextBox udpPortTextBox;
        private System.Windows.Forms.Label udpPortStdLabel;
        private System.Windows.Forms.CheckBox udpMulticastCheckBox;
        private System.Windows.Forms.CheckBox udpOnTopCheckBox;
        private System.Windows.Forms.CheckBox udpDiagLogCheckBox;
        public System.Windows.Forms.GroupBox filterGroupBox;
        private System.Windows.Forms.TextBox subtitleLabel;
        private System.Windows.Forms.TextBox modeLabel;
        private System.Windows.Forms.CheckBox callCqButton;
        private System.Windows.Forms.CheckBox listenButton;
        private System.Windows.Forms.TextBox label12;
        private System.Windows.Forms.CheckBox cqButton;
        private System.Windows.Forms.CheckBox cqDxButton;
        private System.Windows.Forms.TextBox label2;
        private System.Windows.Forms.CheckBox dxButton;
        private System.Windows.Forms.CheckBox nonDxButton;
        private System.Windows.Forms.TextBox label4;
        private System.Windows.Forms.CheckBox potaButton;
        private System.Windows.Forms.CheckBox hunterButton;
        private System.Windows.Forms.TextBox label5;
        private System.Windows.Forms.CheckBox allButton;
        private System.Windows.Forms.CheckBox recentButton;
        private System.Windows.Forms.TextBox label9;
        private System.Windows.Forms.Button okButton;
        private System.Windows.Forms.Button cancelButton;
        private System.Windows.Forms.TabPage advUiTabPage;
        private System.Windows.Forms.Panel advUiPanel;
        private System.Windows.Forms.TabPage wantedCallsTabPage;
        private System.Windows.Forms.Panel wantedCallsPanel;
        private System.Windows.Forms.TabPage spotWatchTabPage;
        private System.Windows.Forms.Panel spotWatchPanel;
        private System.Windows.Forms.TabPage radioTabPage;
        private System.Windows.Forms.Panel radioPanel;
        private System.Windows.Forms.TabPage decodeEngineTabPage;
        private System.Windows.Forms.Panel decodeEnginePanel;
        private System.Windows.Forms.TabPage soundsTabPage;
        private System.Windows.Forms.Panel soundsPanel;
        private System.Windows.Forms.TabPage logbookSyncTabPage;
        private System.Windows.Forms.Panel logbookSyncPanel;
        private System.Windows.Forms.TabPage lookupTabPage;
        private System.Windows.Forms.Panel lookupPanel;
        private System.Windows.Forms.TabPage appearanceTabPage;
        private System.Windows.Forms.Panel appearancePanel;
    }
}
