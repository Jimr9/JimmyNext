using System;

namespace WsjtxUdpLib.Messages.Out
{
    public class StatusMessage : WsjtxMessage
    {
        /*
         * Status        Out       1                      quint32
         *                         Id (unique key)        utf8
         *                         Dial Frequency (Hz)    quint64
         *                         Mode                   utf8
         *                         DX call                utf8
         *                         Report                 utf8
         *                         Tx Mode                utf8
         *                         Tx Enabled             bool
         *                         Transmitting           bool
         *                         Decoding               bool
         *                         Rx DF                  quint32
         *                         Tx DF                  quint32
         *                         DE call                utf8
         *                         DE grid                utf8
         *                         DX grid                utf8
         *                         Tx Watchdog            bool
         *                         Sub-mode               utf8
         *                         Fast mode              bool
         *                         Special Operation Mode quint8
         *                         Frequency Tolerance    quint32
         *                         T/R Period             quint32
         *                         Configuration Name     utf8
         *                         Last Tx Msg            utf8      standard as of a
         *                         QSO Progress           quint32   later WSJT-X release
         *                                                          (confirmed present,
         *                                                          field-tested against
         *                                                          real WSJT-X Improved
         *                                                          3.1, 2026-07-17 --
         *                                                          this comment used to
         *                                                          call both fields
         *                                                          "non-std extension",
         *                                                          which was stale/wrong)
         *                         TxFirst/DblClk/Check/TxHaltClk/TxEnableButton/
         *                         TxEnableClk/MyContinent/MetricUnits (below, in Parse)
         *                         remain genuinely Andy-fork-specific non-standard
         *                         extensions -- a standard-only build's message ends
         *                         right after QSO Progress.
         *
         *    WSJT-X  sends this  status message  when various  internal state
         *    changes to allow the server to  track the relevant state of each
         *    client without the need for  polling commands. The current state
         *    changes that generate status messages are:
         *
         *      Application start up,
         *      "Enable Tx" button status changes,
         *      dial frequency changes,
         *      changes to the "DX Call" field,
         *      operating mode, sub-mode or fast mode changes,
         *      transmit mode changed (in dual JT9+JT65 mode),
         *      changes to the "Rpt" spinner,
         *      after an old decodes replay sequence (see Replay below),
         *      when switching between Tx and Rx mode,
         *      at the start and end of decoding,
         *      when the Rx DF changes,
         *      when the Tx DF changes,
         *      when settings are exited,
         *      when the DX call or grid changes,
         *      when the Tx watchdog is set or reset,
         *      when the frequency tolerance is changed,
         *      when the T/R period is changed,
         *      when the configuration name changes.
         *
         *    The Special operation mode is  an enumeration that indicates the
         *    setting  selected  in  the  WSJT-X  "Settings->Advanced->Special
         *    operating activity" panel. The values are as follows:
         *
         *       0 -> NONE
         *       1 -> NA VHF
         *       2 -> EU VHF
         *       3 -> FIELD DAY
         *       4 -> RTTY RU
         *       5 -> WW DIGI
         *       6 -> FOX
         *       7 -> HOUND
         *
         *    The Frequency Tolerance  and T/R period fields may  have a value
         *    of  the maximum  quint32 value  which implies  the field  is not
         *    applicable.
         */
        // The classic-UDP byte Parse() was removed with the WSJT-X wire-codec cleanup
        // (2026-08-31). The Direct engine transport constructs a StatusMessage directly
        // from its JSON snapshot (see WsjtxClient.Direct.cs) -- these fields are set via
        // an object initializer there, not decoded off a datagram.

        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public ulong DialFrequency { get; set; }
        public string Mode { get; set; }
        public string DxCall { get; set; }
        public string Report { get; set; }
        public string TransmitMode { get; set; }
        public bool TxEnabled { get; set; }
        public bool Transmitting { get; set; }
        public bool Decoding { get; set; }
        public UInt32 RxDF { get; set; }
        public UInt32 TxDF { get; set; }
        public new string DeCall { get; set; }
        public string DeGrid { get; set; }
        public string Detail { get; set; }
        public bool TxWatchdog { get; set; }
        public string Submode { get; set; }
        public bool FastMode { get; set; }
        public SpecialOperationMode SpecialOperationMode { get; set; }
        public uint? ResultCode { get; set; }
        public uint? TRPeriod { get; set; }
        public string ConfigurationName { get; set; }
        public string LastTxMsg { get; set; }
        public UInt32 QsoProgress { get; set; }
        public bool TxFirst { get; set; }
        public bool DblClk { get; set; }
        public string Check { get; set; }
        public bool TxHaltClk { get; set; }
        public bool TxEnableButton { get; set; }
        public bool TxEnableClk { get; set; }
        public string MyContinent { get; set; }
        public bool MetricUnits { get; set; }

        public override string ToString()
            => $"Status     {this.ToCompactLine(nameof(Id))}";
    }

    public enum SpecialOperationMode : byte
    {
        None,
        NaVhf,
        EuVhf,
        FieldDay,
        RttyRu,
        WwDigi,
        Fox,
        Hound
    }
}
