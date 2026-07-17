using System;
using System.Collections.Generic;
using System.Text;

namespace WsjtxUdpLib.Messages.Out
{

    /// <summary>
    /// A .NET type which parses the format of UDP datagrams emitted from WSJT-X on UDP port 2237,
    /// for the Decode message type (the type emitted when WSJT-X decodes an FT8 frame)
    /// </summary>
    public class DecodeMessage : WsjtxMessage
    {
        /*
         * Excerpt from NetworkMessage.hpp in WSJT-X source code:
         * 
         * WSJT-X Message Formats
         * ======================
         *
         * All messages are written or  read using the QDataStream derivatives
         * defined below, note that we are using the default for floating
         * point precision which means all are double precision i.e. 64-bit
         * IEEE format.
         *
         *  Message is big endian format
         *
         *   Header format:
         *
         *      32-bit unsigned integer magic number 0xadbccbda
         *      32-bit unsigned integer schema number
         *
         *   Payload format:
         *
         *      As per  the QDataStream format,  see below for version  used and
         *      here:
         *
         *        http://doc.qt.io/qt-5/datastreamformat.html
         *
         *      for the serialization details for each type, at the time of
         *      writing the above document is for Qt_5_0 format which is buggy
         *      so we use Qt_5_4 format, differences are:
         *
         *      QDateTime:
         *           QDate      qint64    Julian day number
         *           QTime      quint32   Milli-seconds since midnight
         *           timespec   quint8    0=local, 1=UTC, 2=Offset from UTC
         *                                                 (seconds)
         *                                3=time zone
         *           offset     qint32    only present if timespec=2
         *           timezone   several-fields only present if timespec=3
         *
         *      we will avoid using QDateTime fields with time zones for simplicity.
         *
         * Type utf8  is a  utf-8 byte  string formatted  as a  QByteArray for
         * serialization purposes  (currently a quint32 size  followed by size
         * bytes, no terminator is present or counted).
         *
         * The QDataStream format document linked above is not complete for
         * the QByteArray serialization format, it is similar to the QString
         * serialization format in that it differentiates between empty
         * strings and null strings. Empty strings have a length of zero
         * whereas null strings have a length field of 0xffffffff.
         * 
         * Decode        Out       2                      quint32      4 bytes?
         *                         Id (unique key)        utf8         4 bytes, that number of chars, no terminator
         *                         New                    bool         1 byte or bit?
         *                         Time                   QTime        quint32   Milliseconds since midnight (4 bytes?)
         *                         snr                    qint32       4 bytes?
         *                         Delta time (S)         float (serialized as double) 8 bytes
         *                         Delta frequency (Hz)   quint32      4 bytes
         *                         Mode                   utf8         4 bytes, that number of chars, no terminator
         *                         Message                utf8         4 bytes, that number of chars, no terminator
         *                         Low confidence         bool         1 byte or bit?
         *                         Off air                bool         1 byte or bit?
         *
         *      The decode message is sent when  a new decode is completed, in
         *      this case the 'New' field is true. It is also used in response
         *      to  a "Replay"  message where  each  old decode  in the  "Band
         *      activity" window, that  has not been erased, is  sent in order
         *      as a one of these messages  with the 'New' field set to false.
         *      See  the "Replay"  message below  for details  of usage.   Low
         *      confidence decodes are flagged  in protocols where the decoder
         *      has knows that  a decode has a higher  than normal probability
         *      of  being  false, they  should  not  be reported  on  publicly
         *      accessible services  without some attached warning  or further
         *      validation. Off air decodes are those that result from playing
         *      back a .WAV file.
         *      
         * From MessageServer.cpp:

                 case NetworkMessage::Decode:
                 {
                     // unpack message
                     bool is_new {true};
                     QTime time;
                     qint32 snr;
                     float delta_time;
                     quint32 delta_frequency;
                     QByteArray mode;
                     QByteArray message;
                     bool low_confidence {false};
                     bool off_air {false};
                     in >> is_new >> time >> snr >> delta_time >> delta_frequency >> mode >> message >> low_confidence >> off_air;
                     if (check_status (in) != Fail)
                     {
                         Q_EMIT self_->decode (is_new, id, time, snr, delta_time, delta_frequency
                                             , QString::fromUtf8 (mode), QString::fromUtf8 (message)
                                             , low_confidence, off_air);
                     }
                 }
                 break;
         *      
         */

        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        /// <summary>
        /// True when the decode is off-air, false when the decode is from a replay requested from a third-party pplication
        /// </summary>

        //private string[] messageWords;

        public bool New { get; set; }           //true = not a replay of previous decode
        public TimeSpan SinceMidnight { get; set; }
        public DateTime RxDate { get; set; }
        public int Snr { get; set; }
        public double DeltaTime { get; set; }
        public int DeltaFrequency { get; set; }
        public string Mode { get; set; }
        public int Priority { get; set; }
        //  0     1    2      idx
        //K9AVT K4SV R-03
        //01234567890123456789012345678901234567890 posn
        //          1         2         3         4  
        public string Message { get; set; }
        public bool UseStdReply { get; set; }       //do not skip grid msg in reply, even if WSJT-X configured so
        /// <summary>
        ///  True to indicate the decode was derived from a .WAV file playback, false when decoded from an on air reception.
        /// </summary>
        public bool OffAir { get; set; }            //not used, possible spare variable

        public bool IsCallTo(string myCall)
        {
            return myCall != null && myCall == WsjtxMessage.ToCall(Message);
        }

        public bool Is73()
        {
            return WsjtxMessage.Is73(Message);
        }

        public bool IsRR73()
        {
            return WsjtxMessage.IsRR73(Message);
        }

        public bool Is73orRR73()
        {
            return WsjtxMessage.Is73orRR73(Message);
        }

        public bool IsCQ()
        {
            return WsjtxMessage.IsCQ(Message);
        }

        public bool IsReply()
        {
            return WsjtxMessage.IsReply(Message);
        }

        public bool IsRogers()
        {
            return WsjtxMessage.IsRogers(Message);
        }

        public bool IsContest()
        {
            return WsjtxMessage.IsContest(Message);
        }

        public bool IsInvalidType()
        {
            return WsjtxMessage.IsInvalidType(Message);
        }

        public bool IsPota()
        {
            return WsjtxMessage.IsCQ(Message) && WsjtxMessage.IsPota(Message);
        }

        public bool IsSota()
        {
            return WsjtxMessage.IsCQ(Message) && WsjtxMessage.IsSota(Message);
        }

        public bool IsFoxHound()
        {
            return WsjtxMessage.IsFoxHound(Message);
        }

        public string DeCall()
        {
            return WsjtxMessage.DeCall(Message);
        }

        public string ToCall()
        {
            return WsjtxMessage.ToCall(Message);
        }

        public string Payload()
        {
            return WsjtxMessage.Payload(Message);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("Decode     ");
            sb.Append($"{Col(SinceMidnight, 8, Align.Left)} ");
            sb.Append($"{Col(Snr, 3, Align.Right)} ");
            sb.Append($"{Col(DeltaFrequency, 4, Align.Right)} ");
            sb.Append($"{Col(DeltaTime, 4, Align.Right)} ");
            sb.Append($"{Col(Mode, 1, Align.Left)} ");
            sb.Append($"{(UseStdReply ? "SR" : "  ")} ");
            sb.Append($"{Col(Message, 20, Align.Left)} ");
            sb.Append($"{Col(Priority, 1, Align.Left)} ");

            return sb.ToString();
        }

        public static new WsjtxMessage Parse(byte[] message)
        {
            if (!CheckMagicNumber(message))
            {
                return null;
            }

            var decodeMessage = new DecodeMessage();

            int cur = MAGIC_NUMBER_LENGTH;
            decodeMessage.SchemaVersion = DecodeQInt32(message, ref cur);

            var messageType = (MessageType)DecodeQInt32(message, ref cur);

            if (messageType != MessageType.DECODE_MESSAGE_TYPE)
            {
                return null;
            }

            decodeMessage.Id = DecodeString(message, ref cur);
            decodeMessage.New = DecodeBool(message, ref cur);
            decodeMessage.SinceMidnight = DecodeQTime(message, ref cur);
            decodeMessage.RxDate = DateTime.UtcNow.Date;
            decodeMessage.Snr = DecodeQInt32(message, ref cur);
            decodeMessage.DeltaTime = DecodeDouble(message, ref cur);
            decodeMessage.DeltaFrequency = DecodeQInt32(message, ref cur);
            decodeMessage.Mode = DecodeString(message, ref cur);
            decodeMessage.Message = DecodeString(message, ref cur);

            //this actually happens, because of AP (a priori) set
            //'W1AW K1HZ FN42                      ? a2'  (long pad before ?)
            //'KB0UZT LA8ENA +00  ? a3'                   (short pad before ?)
            //use " ?" as delimiter; "?" is never a valid FT8 message character
            int idx = decodeMessage.Message.IndexOf(" ?");
            if (idx != -1)
            {
                decodeMessage.Message = decodeMessage.Message.Substring(0, idx).TrimEnd();
            }

            //WSJT-X 3.0 drops the "?" and appends only the AP suffix, e.g. " a35" or " a3"
            //'KI4QMB KE9DMW -15 a35'  -> strip to 'KI4QMB KE9DMW -15'
            {
                string m = decodeMessage.Message;
                int i = m.Length - 1;
                while (i >= 0 && char.IsDigit(m[i])) i--;
                if (i < m.Length - 1 && i >= 1 && m[i] == 'a' && m[i - 1] == ' ')
                    decodeMessage.Message = m.Substring(0, i - 1).TrimEnd();
            }

            //hashed message case, brackets and only two words:
            // <K1JT> KG6EMU/AG
            decodeMessage.Message = RemoveAngleBrackets(decodeMessage.Message);

            decodeMessage.UseStdReply = false; //used in ReplyToCq, was: DecodeBool(message, ref cur);
            decodeMessage.OffAir = DecodeBool(message, ref cur);
            decodeMessage.Priority = (int)WSJTX_Controller.WsjtxClient.CallPriority.DEFAULT;

            //decodeMessage.messageWords = decodeMessage.Message.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            return decodeMessage;
        }
    }



    //used to notify Controller of message manually selected in WSJT-X to be added to reply list,
    //or a CQ notification automatically generated by WSJT-X for evaluation as a possible message for the reply list 
    public class EnqueueDecodeMessage : DecodeMessage
    {
        public enum Qualities
        {
            NONE,
            LOW,
            MARGINAL,
            MEDIUM,
            HIGH
        }
        private string _country = "";
        private string _continent = "";

        public bool Modifier { get; set; }      //false = Alt key, true = Ctrl + Alt keys
        public bool AutoGen { get; set; }       //only CQ messages, and sent w/o manual intervention in WSJT-X
        public bool IsDx { get; set; }          //true = different continent from this QTH
        public bool IsNewCallOnBand { get; set; }     //true = new call sign current band
        public bool IsNewCallAnyBand { get; set; }     //true = new call sign any band
        public bool IsNewCountry { get; set; }  //any message type, true = new country any band
        public bool IsNewCountryOnBand { get; set; }  //any message type, true = new country on band
        public string Country {

            get => _country;

            set
            {
                _country = WsjtxCountry(value);
            }
}
        public string Continent 
        {
            get => _continent;

            set
            {
                if (value == null)
                {
                    _continent = "";
                }
                else
                {
                    _continent = value;
                }
            }
        }
        public int Distance { get; set; }
        public int Azimuth { get; set; }
        public int Rank { get; set; }
        public int SequenceNumber { get; set; }
        public int Quality { get; set; }
        public WSJTX_Controller.WsjtxClient.CallCategory Category { get; set; }
        // Id of whichever actively-checked Rule Definition this message matched, if any --
        // set by DeriveCategory/CheckAwardAlert, read back for display (CategoryTag/raw
        // decode tags) and for smart sound file lookup. Null when no award matched.
        public string MatchedAwardRuleId { get; set; }
        // Stage A6 (migration roadmap): the ClassifiedCall computed by ClassificationEngine
        // for this decode, attached once by ProcessDecodeMsg before any classification-
        // derived field below is read. The wire-supplied fields above (IsDx/IsNewCallOnBand/
        // etc.) are left completely untouched alongside this -- deliberately, so the two can
        // always be diffed directly for parity diagnosis. Null until ProcessDecodeMsg runs
        // (e.g. messages that never reach it). Use EnqueueDecodeMessage.EffectiveClassification()
        // (Classification/ClassificationCutover.cs) rather than reading this directly.
        public WSJTX_Controller.ClassifiedCall Classified { get; set; }

        // Minor words a title-caser should leave lowercase (mid-string) rather than
        // capitalizing every word blindly -- e.g. "Isle of Man", not "Isle Of Man".
        private static readonly HashSet<string> CountryMinorWords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "of", "the", "and" };

        public static string WsjtxCountry(string country)
        {
            //match usage in WSJT-X
            if (string.IsNullOrEmpty(country)) return "";

            // Specific short names/abbreviations WSJT-X displays that aren't just a
            // title-cased version of the source data's own name (e.g. "USA" not
            // "United States Of America") -- checked case-insensitively so this
            // matches regardless of which upstream convention produced the string:
            // Club Log's ALL-CAPS canonical <name> (the primary source since Stage
            // A6 field testing 2026-07-15/16), a QRZ-style mixed-case value ("United
            // States"), or an already-normalized value (test fixtures, a value that
            // already passed through here once). Found via live A6 field testing
            // 2026-07-16/17: the original checks here only ever matched a QRZ-style
            // string Club Log's data never produces, so every country fell through
            // unnormalized once Club Log became the primary Country source.
            switch (country.ToUpperInvariant())
            {
                case "USA":
                case "UNITED STATES":
                case "UNITED STATES OF AMERICA":
                    return "USA";
                case "GERMANY":
                case "FED. REP. OF GERMANY":
                case "FEDERAL REPUBLIC OF GERMANY":
                    return "Germany";
                case "EU RUSSIA":
                case "EUROPEAN RUSSIA":
                    return "EU Russia";
                case "AS RUSSIA":
                case "ASIATIC RUSSIA":
                    return "AS Russia";
                case "EU TURKEY":
                case "EUROPEAN TURKEY":
                    return "EU Turkey";
                case "AS TURKEY":
                case "ASIATIC TURKEY":
                    return "AS Turkey";
            }

            // General case: Club Log's <name> is ALL CAPS ("ISLE OF MAN", "CZECH
            // REPUBLIC"); WSJT-X displays ordinary title case ("Isle of Man", "Czech
            // Republic"). Lowercase first, then capitalize each word's first letter --
            // .NET's own ToTitleCase deliberately leaves already-all-uppercase words
            // unchanged (treating them as acronyms), which would be a no-op on this
            // input, so that's not used here.
            var words = country.ToLowerInvariant().Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length == 0) continue;
                if (i > 0 && CountryMinorWords.Contains(words[i])) continue;
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            }
            return string.Join(" ", words);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("EnqDecode: ");
            sb.Append($"{Col(SinceMidnight, 8, Align.Left)} ");
            sb.Append($"{Col(Snr, 3, Align.Right)} ");
            sb.Append($"{Col(DeltaFrequency, 4, Align.Right)} ");
            sb.Append($"{Col(DeltaTime, 4, Align.Right)} ");
            sb.Append($"{Col(Mode, 1, Align.Left)} ");
            sb.Append($"sr:{(UseStdReply ? "T " : "F ")} ");
            sb.Append($"{Col(Message, 20, Align.Left)} ");
            sb.Append($"md:{Col(Modifier, 1, Align.Left)} ");
            sb.Append($"au:{Col(AutoGen, 1, Align.Left)} ");
            sb.Append($"dx:{Col(IsDx, 1, Align.Left)} ");
            sb.Append($"ncb:{Col(IsNewCallOnBand, 1, Align.Left)} ");
            sb.Append($"nc:{Col(IsNewCallAnyBand, 1, Align.Left)} ");
            sb.Append($"neb:{Col(IsNewCountryOnBand, 1, Align.Left)} ");
            sb.Append($"ne:{Col(IsNewCountry, 1, Align.Left)} ");
            sb.Append($"pr:{Col(Priority, 1, Align.Left)} ");
            sb.Append($"{Col(Country, 6, Align.Left)} ");
            sb.Append($"{Col(Continent, 2, Align.Left)} ");
            sb.Append($"{Col(Distance, 5, Align.Left)} ");
            sb.Append($"{Col(Azimuth, 3, Align.Left)} ");
            sb.Append($"{Col(Quality, 3, Align.Left)} ");

            return sb.ToString().Replace("   ", " ").Replace("  ", " ");
        }
        public static new WsjtxMessage Parse(byte[] message)
        {
            if (!CheckMagicNumber(message))
            {
                return null;
            }

            var enqueueDecodeMessage = new EnqueueDecodeMessage();

            int cur = MAGIC_NUMBER_LENGTH;
            enqueueDecodeMessage.SchemaVersion = DecodeQInt32(message, ref cur);

            var messageType = (MessageType)DecodeQInt32(message, ref cur);

            if ((WSJTX_Controller.WsjtxClient.IsWsjtx270Rc() && messageType != MessageType.ENQUEUE_DECODE_MESSAGE_TYPE_2)
                || (!WSJTX_Controller.WsjtxClient.IsWsjtx270Rc() && messageType != MessageType.ENQUEUE_DECODE_MESSAGE_TYPE_3))
            {
                return null;
            }

            enqueueDecodeMessage.Id = DecodeString(message, ref cur);
            enqueueDecodeMessage.AutoGen = DecodeBool(message, ref cur);
            enqueueDecodeMessage.SinceMidnight = DecodeQTime(message, ref cur);
            enqueueDecodeMessage.RxDate = DateTime.UtcNow.Date;
            enqueueDecodeMessage.Snr = DecodeQInt32(message, ref cur);
            enqueueDecodeMessage.DeltaTime = DecodeDouble(message, ref cur);
            enqueueDecodeMessage.DeltaFrequency = DecodeQInt32(message, ref cur);
            enqueueDecodeMessage.Mode = DecodeString(message, ref cur);
            enqueueDecodeMessage.Message = DecodeString(message, ref cur);

            //this actually happens, because of AP (a priori) set
            //'W1AW K1HZ FN42                      ? a2'  (long pad before ?)
            //'KB0UZT LA8ENA +00  ? a3'                   (short pad before ?)
            //use " ?" as delimiter; "?" is never a valid FT8 message character
            int idx = enqueueDecodeMessage.Message.IndexOf(" ?");
            if (idx != -1)
            {
                enqueueDecodeMessage.Message = enqueueDecodeMessage.Message.Substring(0, idx).TrimEnd();
            }

            //WSJT-X 3.0 drops the "?" and appends only the AP suffix, e.g. " a35" or " a3"
            //'KI4QMB KE9DMW -15 a35'  -> strip to 'KI4QMB KE9DMW -15'
            {
                string m = enqueueDecodeMessage.Message;
                int i = m.Length - 1;
                while (i >= 0 && char.IsDigit(m[i])) i--;
                if (i < m.Length - 1 && i >= 1 && m[i] == 'a' && m[i - 1] == ' ')
                    enqueueDecodeMessage.Message = m.Substring(0, i - 1).TrimEnd();
            }

            //hashed message case, brackets and only two words:
            // <K1JT> KG6EMU/AG
            enqueueDecodeMessage.Message = RemoveAngleBrackets(enqueueDecodeMessage.Message);

            enqueueDecodeMessage.UseStdReply = false;  //used in ReplyToCq
            enqueueDecodeMessage.IsDx = DecodeBool(message, ref cur);
            enqueueDecodeMessage.Modifier = DecodeBool(message, ref cur);
            enqueueDecodeMessage.IsNewCallOnBand = DecodeBool(message, ref cur);
            enqueueDecodeMessage.IsNewCallAnyBand = DecodeBool(message, ref cur);
            enqueueDecodeMessage.IsNewCountryOnBand = DecodeBool(message, ref cur);
            enqueueDecodeMessage.IsNewCountry = DecodeBool(message, ref cur);
            enqueueDecodeMessage.Priority = (int)WSJTX_Controller.WsjtxClient.CallPriority.DEFAULT;      //set here temporarily
            enqueueDecodeMessage.Country = DecodeString(message, ref cur);
            enqueueDecodeMessage.Continent = DecodeString(message, ref cur);
            enqueueDecodeMessage.Azimuth = DecodeQInt32(message, ref cur);
            enqueueDecodeMessage.Distance = DecodeQInt32(message, ref cur);

            enqueueDecodeMessage.SequenceNumber = 0;
            enqueueDecodeMessage.SetMsgQuality();

            return enqueueDecodeMessage;
        }

        // Found via live A7 field testing 2026-07-17: Jimmy's decode-processing code
        // (WsjtxClient.Protocol.cs's Update()) only ever reacted to this class's own
        // Parse() above -- the non-standard, Andy-fork-specific EnqueueDecodeMessage
        // (msg type 18). Stock WSJT-X and WSJT-X Improved never send that message type
        // at all; they send the plain, standard base-class Decode message (msg type 2,
        // DecodeMessage.Parse() above) instead. Stage A6 already made every downstream
        // consumer (ProcessDecodeMsg, CallQueueRanker, AwardTagger, etc.) read computed
        // classification via EffectiveClassification() rather than this class's own
        // wire-supplied fields -- so the fix here is narrow: adapt a standard
        // DecodeMessage into an EnqueueDecodeMessage shell so it can flow through that
        // exact same pipeline unchanged, rather than needing a second, parallel
        // processing path.
        //
        // Every field below is either (a) copied straight from the base DecodeMessage
        // fields a standard Decode broadcast genuinely carries, (b) safe to leave at
        // its default because ProcessDecodeMsg/downstream code always overwrites it
        // before any read (Rank/SequenceNumber/Category/MatchedAwardRuleId, and the
        // raw wire-supplied IsDx/IsNewCallOnBand/Country/Continent/Azimuth/Distance
        // fields, none of which any consumer reads directly -- only
        // EffectiveClassification(), which uses the freshly Classifier.Classify()-
        // computed value regardless of source), or (c) AutoGen/Quality, which need a
        // real value because a standard Decode broadcast has no wire-supplied
        // equivalent to copy:
        //   - AutoGen=true: a standard Decode broadcast is *always* an automatic
        //     decode -- there's no "manually enqueued in WSJT-X's own UI" concept in
        //     the standard protocol (that's what Andy's fork's AutoGen=false case
        //     represents). ProcessDecodeMsg's "toMyCall && AutoGen" branch is the
        //     entire path that handles a station actively replying to me; AutoGen=false
        //     here would silently route every reply through the generic
        //     AddSelectedCall path instead, losing that handling.
        //   - Quality: computed the same way Parse()/SetMsgQuality() always compute
        //     it for a real EnqueueDecodeMessage too -- purely from Message content,
        //     never wire-supplied even in the non-standard case -- so it's replicated
        //     here rather than left at the Qualities.NONE default UpdateCallQueue
        //     would otherwise misread as a low-quality decode worth evicting.
        public static EnqueueDecodeMessage FromStandardDecode(DecodeMessage src)
        {
            var enqueueDecodeMessage = new EnqueueDecodeMessage
            {
                SchemaVersion = src.SchemaVersion,
                Id = src.Id,
                New = src.New,
                SinceMidnight = src.SinceMidnight,
                RxDate = src.RxDate,
                Snr = src.Snr,
                DeltaTime = src.DeltaTime,
                DeltaFrequency = src.DeltaFrequency,
                Mode = src.Mode,
                Message = src.Message,
                Priority = src.Priority,
                UseStdReply = src.UseStdReply,
                OffAir = src.OffAir,
                AutoGen = true,
            };
            enqueueDecodeMessage.SetMsgQuality();
            return enqueueDecodeMessage;
        }

        public EnqueueDecodeMessage DeepCopy()
        {
            var enqueueDecodeMessage = new EnqueueDecodeMessage();
            enqueueDecodeMessage.Id = CopyString(Id);
            enqueueDecodeMessage.AutoGen = AutoGen;
            enqueueDecodeMessage.SinceMidnight = new TimeSpan(SinceMidnight.Ticks);
            enqueueDecodeMessage.RxDate = new DateTime(RxDate.Ticks);
            enqueueDecodeMessage.Snr = Snr;
            enqueueDecodeMessage.DeltaTime = DeltaTime;
            enqueueDecodeMessage.DeltaFrequency = DeltaFrequency;
            enqueueDecodeMessage.Mode = CopyString(Mode);
            enqueueDecodeMessage.Message = CopyString(Message);
            enqueueDecodeMessage.UseStdReply = UseStdReply;
            enqueueDecodeMessage.IsDx = IsDx;
            enqueueDecodeMessage.Modifier = Modifier;
            enqueueDecodeMessage.IsNewCallOnBand = IsNewCallOnBand;
            enqueueDecodeMessage.IsNewCallAnyBand = IsNewCallAnyBand;
            enqueueDecodeMessage.IsNewCountryOnBand = IsNewCountryOnBand;
            enqueueDecodeMessage.IsNewCountry = IsNewCountry;
            enqueueDecodeMessage.Priority = Priority;
            enqueueDecodeMessage.Category = Category;
            enqueueDecodeMessage.MatchedAwardRuleId = MatchedAwardRuleId;
            enqueueDecodeMessage.Country = CopyString(Country);
            enqueueDecodeMessage.Continent = CopyString(Continent);
            enqueueDecodeMessage.Azimuth = Azimuth;
            enqueueDecodeMessage.Distance = Distance;
            enqueueDecodeMessage.SequenceNumber = SequenceNumber;
            enqueueDecodeMessage.Quality = Quality;
            enqueueDecodeMessage.Rank = Rank;

            return enqueueDecodeMessage;
        }

        // String.Copy(string) is obsolete on modern .NET. Strings are immutable, so a
        // distinct copy is never observable; only String.Copy's null-check behavior
        // (ArgumentNullException on a null field) needs preserving here.
        private static string CopyString(string str) => str ?? throw new ArgumentNullException(nameof(str));

        //quality is how likely a station is to answer while in the current stage of a QSO
        private void SetMsgQuality()
        {
            if (IsCQ()) Quality = (int)Qualities.HIGH;
            else if (Is73orRR73()) Quality = (int)Qualities.MEDIUM;
            else if (IsRogers()) Quality = (int)Qualities.MARGINAL;
            else Quality = (int)Qualities.LOW;
        }

    }
}
