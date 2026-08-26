
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
            this._categoryListBox = new System.Windows.Forms.ListBox();
            this._categoryDetailHost = new System.Windows.Forms.Panel();
            this.logbookSyncPanel   = new System.Windows.Forms.Panel();
            this.lookupPanel   = new System.Windows.Forms.Panel();
            this.generalPanel = new System.Windows.Forms.Panel();
            this.receiveReplyPanel = new System.Windows.Forms.Panel();
            this.rcvCallingGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvReplyingGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvDirectedCqGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvReplyBehaviorGroupBox = new System.Windows.Forms.GroupBox();
            this.rcvBlockListGroupBox = new System.Windows.Forms.GroupBox();
            this.transmitPanel = new System.Windows.Forms.Panel();
            this.rcvTransmitGroupBox = new System.Windows.Forms.GroupBox();
            this.basicPanel = new System.Windows.Forms.Panel();
            this.hotkeysPanel = new System.Windows.Forms.Panel();
            this.advUiPanel = new System.Windows.Forms.Panel();
            this.wantedCallsPanel = new System.Windows.Forms.Panel();
            this.spotWatchPanel = new System.Windows.Forms.Panel();
            this.radioPanel = new System.Windows.Forms.Panel();
            this.decodeEnginePanel = new System.Windows.Forms.Panel();
            this.decodePanel = new System.Windows.Forms.Panel();
            this.frequenciesPanel = new System.Windows.Forms.Panel();
            this.notificationsPanel = new System.Windows.Forms.Panel();
            this.soundsPanel = new System.Windows.Forms.Panel();
            this.appearancePanel = new System.Windows.Forms.Panel();
            this.profilesPanel = new System.Windows.Forms.Panel();
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
            this._categoryDetailHost.SuspendLayout();
            this.generalPanel.SuspendLayout();
            this.receiveReplyPanel.SuspendLayout();
            this.transmitPanel.SuspendLayout();
            this.basicPanel.SuspendLayout();
            this.advUiPanel.SuspendLayout();
            this.wantedCallsPanel.SuspendLayout();
            this.spotWatchPanel.SuspendLayout();
            this.radioPanel.SuspendLayout();
            this.decodeEnginePanel.SuspendLayout();
            this.decodePanel.SuspendLayout();
            this.frequenciesPanel.SuspendLayout();
            this.notificationsPanel.SuspendLayout();
            this.soundsPanel.SuspendLayout();
            this.appearancePanel.SuspendLayout();
            this.profilesPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // _categoryListBox
            //
            // Replaces tabControl1 (2026-08-10, Options accessibility reorg): the native
            // TabControl/TabPage container re-announces itself noisily on every tab switch with
            // a real screen reader. A ListBox driving one visible detail panel at a time is the
            // same mechanism WireServiceList() already uses successfully inside the Logbook Sync
            // and Lookup Data panels below (3-4 items there) -- proven with JAWS/NVDA in this
            // app already, just scaled up to all 16 categories here. Mechanical container swap
            // only: every category's own controls/AccessibleName/Build*Tab()/Save*Tab() are
            // completely unchanged, only how the operator selects which one is visible changes.
            this._categoryListBox.AccessibleName = "Options categories";
            this._categoryListBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F);
            this._categoryListBox.FormattingEnabled = true;
            this._categoryListBox.IntegralHeight = false;
            this._categoryListBox.Items.AddRange(new object[] {
                "Basic",
                "General",
                "Receive / Auto Reply",
                "Transmit",
                "Hotkeys",
                "Advanced UI",
                "Wanted Calls",
                "Spot Watch",
                "Sounds",
                "Radio",
                "Decode Engine",
                "Decode",
                "Frequencies",
                "Notifications",
                "Logbook Sync",
                "Lookup Data",
                "Appearance",
                "Profiles"});
            this._categoryListBox.Location = new System.Drawing.Point(0, 0);
            this._categoryListBox.Name = "_categoryListBox";
            this._categoryListBox.Size = new System.Drawing.Size(190, 380);
            this._categoryListBox.TabIndex = 0;
            //
            // _categoryDetailHost
            //
            // Each category's own real content panel (unchanged from today) is added here
            // dynamically at runtime -- only the one matching _categoryListBox's current
            // selection is ever actually parented here at a time (see WireCategoryList in
            // OptionsDlg.cs). Previously all 16 panels were added here unconditionally and
            // toggled via Visible, but live JAWS testing showed stale text from
            // previously-selected panels bleeding into the announced content of the newly
            // selected one -- JAWS's accessibility cache does not reliably invalidate for a
            // hidden-but-still-parented sibling. WireCategoryList now Adds/Removes the active
            // panel instead, matching the same fix already applied to WireServiceList.
            // basicPanel was originally the bare basicTabPage (no separate inner panel -- its
            // controls, including subtitleLabel, were direct TabPage children) -- converted to a
            // plain Panel here, confirmed live that WinForms' TabPage throws ArgumentException if
            // reparented to anything other than a real TabControl, despite technically inheriting
            // from Panel. Order matches today's actual tab order (confirmed from the old
            // tabControl1.Controls.Add sequence) so nothing about "what's first" changes --
            // basicPanel stays index 0, matching subtitleLabel's unconditional .Focus() at the
            // end of OptionsDlg_Load.
            this._categoryDetailHost.Location = new System.Drawing.Point(196, 0);
            this._categoryDetailHost.Name = "_categoryDetailHost";
            this._categoryDetailHost.Size = new System.Drawing.Size(634, 380);
            this._categoryDetailHost.TabIndex = 1;
            //
            // generalPanel
            //
            this.generalPanel.AccessibleName = "General";
            this.generalPanel.Controls.Add(this.udpOnTopCheckBox);
            this.generalPanel.Controls.Add(this.udpDiagLogCheckBox);
            this.generalPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.generalPanel.Name = "generalPanel";
            //
            // receiveReplyPanel
            //
            this.receiveReplyPanel.AccessibleName = "Receive / Auto Reply";
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
            // transmitPanel
            //
            this.transmitPanel.AccessibleName = "Transmit";
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
            // basicPanel
            //
            this.basicPanel.AccessibleName = "Basic";
            this.basicPanel.Controls.Add(this.subtitleLabel);
            this.basicPanel.Controls.Add(this.modeLabel);
            this.basicPanel.Controls.Add(this.callCqButton);
            this.basicPanel.Controls.Add(this.listenButton);
            this.basicPanel.Controls.Add(this.label12);
            this.basicPanel.Controls.Add(this.cqButton);
            this.basicPanel.Controls.Add(this.cqDxButton);
            this.basicPanel.Controls.Add(this.label2);
            this.basicPanel.Controls.Add(this.dxButton);
            this.basicPanel.Controls.Add(this.nonDxButton);
            this.basicPanel.Controls.Add(this.label4);
            this.basicPanel.Controls.Add(this.potaButton);
            this.basicPanel.Controls.Add(this.hunterButton);
            this.basicPanel.Controls.Add(this.label5);
            this.basicPanel.Controls.Add(this.allButton);
            this.basicPanel.Controls.Add(this.recentButton);
            this.basicPanel.Controls.Add(this.filterGroupBox);
            this.basicPanel.Controls.Add(this.label9);
            this.basicPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.basicPanel.Name = "basicPanel";
            //
            // hotkeysPanel
            //
            this.hotkeysPanel.AccessibleName = "Hotkeys";
            this.hotkeysPanel.AutoScroll = true;
            this.hotkeysPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.hotkeysPanel.Name = "hotkeysPanel";
            //
            // advUiPanel
            //
            this.advUiPanel.AccessibleName = "Advanced UI";
            this.advUiPanel.AutoScroll = true;
            this.advUiPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.advUiPanel.Name = "advUiPanel";
            //
            // wantedCallsPanel
            //
            this.wantedCallsPanel.AccessibleName = "Wanted Calls";
            this.wantedCallsPanel.AutoScroll = true;
            this.wantedCallsPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.wantedCallsPanel.Name = "wantedCallsPanel";
            //
            // spotWatchPanel
            //
            this.spotWatchPanel.AccessibleName = "Spot Watch";
            this.spotWatchPanel.AutoScroll = true;
            this.spotWatchPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.spotWatchPanel.Name = "spotWatchPanel";
            //
            // radioPanel
            //
            this.radioPanel.AccessibleName = "Radio";
            this.radioPanel.AutoScroll = true;
            this.radioPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.radioPanel.Name = "radioPanel";
            // Split out from radioPanel: the Decode Engine section (Jimmy Native / My Call /
            // My Grid / audio device pickers) had grown tall enough to render below the Radio
            // category's visible area -- AutoScroll being set did not make it reachable by Tab
            // with a real screen reader (confirmed live with JAWS/NVDA, 2026-08-06), so it now
            // gets its own category with plenty of room instead of relying on scrolling within
            // Radio's own panel.
            //
            // decodeEnginePanel
            //
            this.decodeEnginePanel.AccessibleName = "Decode Engine";
            this.decodeEnginePanel.AutoScroll = true;
            this.decodeEnginePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.decodeEnginePanel.Name = "decodeEnginePanel";
            // WSJT-X's own decode-related settings (Fast/Normal/Deep depth, F Low/F High, Enable
            // AP, AP-CQ-only, Single decode) -- ported over in Nexus but never previously exposed
            // to Jimmy. Deliberately its own category, separate from decodeEnginePanel above
            // (which configures the always-on native engine itself -- My Call/My Grid, audio
            // devices, DX Cluster server -- not WSJT-X's own decode-quality settings).
            //
            // decodePanel
            //
            this.decodePanel.AccessibleName = "Decode";
            this.decodePanel.AutoScroll = true;
            this.decodePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.decodePanel.Name = "decodePanel";
            // Per-band FT8/FT4 calling-frequency overrides -- WSJT-X's own Settings ▸
            // Frequencies, applied to Jimmy's own Band Up/Down model (one canonical frequency
            // per band, via bandToFreq() in WsjtxClient.BandAudio.cs).
            //
            // frequenciesPanel
            //
            this.frequenciesPanel.AccessibleName = "Frequencies";
            this.frequenciesPanel.AutoScroll = true;
            this.frequenciesPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.frequenciesPanel.Name = "frequenciesPanel";
            //
            // notificationsPanel
            //
            this.notificationsPanel.AccessibleName = "Notifications";
            this.notificationsPanel.AutoScroll = true;
            this.notificationsPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.notificationsPanel.Name = "notificationsPanel";
            //
            // soundsPanel
            //
            this.soundsPanel.AccessibleName = "Sounds";
            this.soundsPanel.AutoScroll = true;
            this.soundsPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.soundsPanel.Name = "soundsPanel";
            //
            // appearancePanel
            //
            this.appearancePanel.AccessibleName = "Appearance";
            this.appearancePanel.AutoScroll = true;
            this.appearancePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.appearancePanel.Name = "appearancePanel";
            //
            // profilesPanel
            //
            this.profilesPanel.AccessibleName = "Profiles";
            this.profilesPanel.AutoScroll = true;
            this.profilesPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.profilesPanel.Name = "profilesPanel";
            //
            // logbookSyncPanel
            //
            this.logbookSyncPanel.AccessibleName = "Logbook Sync";
            this.logbookSyncPanel.AutoScroll = true;
            this.logbookSyncPanel.Dock       = System.Windows.Forms.DockStyle.Fill;
            this.logbookSyncPanel.Name       = "logbookSyncPanel";
            //
            // lookupPanel
            //
            this.lookupPanel.AccessibleName = "Lookup Data";
            this.lookupPanel.AutoScroll = true;
            this.lookupPanel.Dock       = System.Windows.Forms.DockStyle.Fill;
            this.lookupPanel.Name       = "lookupPanel";
            //
            // udpOnTopCheckBox  (General tab / generalPanel)
            //
            this.udpOnTopCheckBox.AccessibleName = "Always on top";
            this.udpOnTopCheckBox.AutoSize = true;
            this.udpOnTopCheckBox.Location = new System.Drawing.Point(10, 15);
            this.udpOnTopCheckBox.Name = "udpOnTopCheckBox";
            this.udpOnTopCheckBox.TabIndex = 0;
            this.udpOnTopCheckBox.Text = "Always on top";
            this.udpOnTopCheckBox.UseVisualStyleBackColor = true;
            //
            // udpDiagLogCheckBox  (General tab / generalPanel)
            //
            this.udpDiagLogCheckBox.AccessibleName = "Log diagnostic info";
            this.udpDiagLogCheckBox.AutoSize = true;
            this.udpDiagLogCheckBox.Location = new System.Drawing.Point(200, 15);
            this.udpDiagLogCheckBox.Name = "udpDiagLogCheckBox";
            this.udpDiagLogCheckBox.TabIndex = 1;
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
            this.okButton.AccessibleName = "OK";
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
            this.cancelButton.AccessibleName = "Cancel";
            this.cancelButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Bold);
            this.cancelButton.Location = new System.Drawing.Point(723, 386);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(100, 27);
            this.cancelButton.TabIndex = 2;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            // T10A fix, 2026-08-23 (HIGHLY LIKELY bug): DialogResult.Cancel was never assigned
            // to this button, and the form's own CancelButton property (below) was never set --
            // OptionsDlg_KeyDown's Escape handler still closed the form correctly, but with no
            // control registered as the form's real Cancel action, a focused child control could
            // participate in Escape/default-button key processing on its own first, plausibly
            // producing the reported Windows default beep even though the form still closed.
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            //
            // OptionsDlg
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(830, 418);
            this.Controls.Add(this._categoryListBox);
            this.Controls.Add(this._categoryDetailHost);
            this.Controls.Add(this.okButton);
            this.Controls.Add(this.cancelButton);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.KeyPreview = true;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            // T10A fix, 2026-08-23: see cancelButton's own comment above -- the form-level
            // CancelButton property was never set at all.
            this.CancelButton = this.cancelButton;
            this.Name = "OptionsDlg";
            this.Text = "Jimmy Options";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.OptionsDlg_FormClosing);
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.OptionsDlg_FormClosed);
            this.Load += new System.EventHandler(this.OptionsDlg_Load);
            this.KeyDown += new System.Windows.Forms.KeyEventHandler(this.OptionsDlg_KeyDown);
            this._categoryDetailHost.ResumeLayout(false);
            this.generalPanel.ResumeLayout(false);
            this.generalPanel.PerformLayout();
            this.receiveReplyPanel.ResumeLayout(false);
            this.transmitPanel.ResumeLayout(false);
            this.basicPanel.ResumeLayout(false);
            this.basicPanel.PerformLayout();
            this.advUiPanel.ResumeLayout(false);
            this.wantedCallsPanel.ResumeLayout(false);
            this.spotWatchPanel.ResumeLayout(false);
            this.radioPanel.ResumeLayout(false);
            this.decodeEnginePanel.ResumeLayout(false);
            this.decodePanel.ResumeLayout(false);
            this.frequenciesPanel.ResumeLayout(false);
            this.notificationsPanel.ResumeLayout(false);
            this.soundsPanel.ResumeLayout(false);
            this.appearancePanel.ResumeLayout(false);
            this.profilesPanel.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.ListBox _categoryListBox;
        private System.Windows.Forms.Panel _categoryDetailHost;
        private System.Windows.Forms.Panel generalPanel;
        private System.Windows.Forms.Panel receiveReplyPanel;
        public System.Windows.Forms.GroupBox rcvCallingGroupBox;
        public System.Windows.Forms.GroupBox rcvReplyingGroupBox;
        public System.Windows.Forms.GroupBox rcvDirectedCqGroupBox;
        public System.Windows.Forms.GroupBox rcvReplyBehaviorGroupBox;
        public System.Windows.Forms.GroupBox rcvBlockListGroupBox;
        private System.Windows.Forms.Panel transmitPanel;
        public System.Windows.Forms.GroupBox rcvTransmitGroupBox;
        private System.Windows.Forms.Panel basicPanel;
        private System.Windows.Forms.Panel hotkeysPanel;
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
        private System.Windows.Forms.Panel advUiPanel;
        private System.Windows.Forms.Panel wantedCallsPanel;
        private System.Windows.Forms.Panel spotWatchPanel;
        private System.Windows.Forms.Panel radioPanel;
        private System.Windows.Forms.Panel decodeEnginePanel;
        private System.Windows.Forms.Panel decodePanel;
        private System.Windows.Forms.Panel frequenciesPanel;
        private System.Windows.Forms.Panel notificationsPanel;
        private System.Windows.Forms.Panel soundsPanel;
        private System.Windows.Forms.Panel logbookSyncPanel;
        private System.Windows.Forms.Panel lookupPanel;
        private System.Windows.Forms.Panel appearancePanel;
        private System.Windows.Forms.Panel profilesPanel;
    }
}
