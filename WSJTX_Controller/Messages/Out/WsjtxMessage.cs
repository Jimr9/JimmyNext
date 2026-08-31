using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace WsjtxUdpLib.Messages.Out
{
    /*
            string s = WsjtxMessage.DirectedTo("CQ K1JT EM51");
            s = WsjtxMessage.DeCall("CQ K1JT EM51");
            s = WsjtxMessage.ToCall("CQ K1JT EM51");

            s = WsjtxMessage.DirectedTo("CQ NA AN K1JT EM51");
            s = WsjtxMessage.DeCall("CQ NA AN K1JT EM51");
            s = WsjtxMessage.ToCall("CQ NA AN K1JT EM51");

            s = WsjtxMessage.DirectedTo("CQ 250 250 K1JT EM51");
            s = WsjtxMessage.DeCall("CQ 250 250 K1JT EM51");
            s = WsjtxMessage.ToCall("CQ 250 250 K1JT EM51");

            s = WsjtxMessage.DirectedTo("CQ N25 K1JT");
            s = WsjtxMessage.DeCall("CQ N25 K1JT");
            s = WsjtxMessage.ToCall("CQ N25 K1JT");

            s = WsjtxMessage.DirectedTo("CQ --- K1JT");
            s = WsjtxMessage.DeCall("CQ --- K1JT");
            s = WsjtxMessage.ToCall("CQ --- K1JT");

            s = WsjtxMessage.DirectedTo("WM8Q K1JT EM51");
            s = WsjtxMessage.DeCall("WM8Q K1JT EM51");
            s = WsjtxMessage.ToCall("WM8Q K1JT EM51");


            s = WsjtxMessage.DirectedTo("CQ USA K1JT EM51");
            s = WsjtxMessage.DeCall("CQ USA K1JT EM51");
            s = WsjtxMessage.ToCall("CQ USA K1JT EM51");

            s = WsjtxMessage.DirectedTo("CQ 250 K1JT EM51");
            s = WsjtxMessage.DeCall("CQ 250 K1JT EM51");
            s = WsjtxMessage.ToCall("CQ 250 K1JT EM51");

    */

    public abstract class WsjtxMessage
    {
        public static string UniqueId = "ExtCtl";
        public static string PgmVersion = "1.0.0";
        private static int maxBaseCallsignLength = 10;
        private static int maxCallDigits = 3;

        public enum NegoStates
        {
            WAIT,
            INITIAL,
            FAIL,
            SENT,
            RECD 
        }

        public static int NegotiatedSchemaVersion = 2;
        public static NegoStates NegoState = NegoStates.INITIAL;

        private static string alphaOnly = "[^A-Za-z]";         //match if any numeric
        private static string numericOnly = "[^0-9]";          //match if any alpha


        //return the "to" call from the msg in the form "W1AW K1JT FN60"
        //if a CQ return "CQ", if no/invalid/non-std msg, return null
        public static string ToCall(string msg)
        {
            if (IsInvalid(msg)) return null;
            string[] words = msg.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() < 2 || words.Count() > 4) return null;
            if (words[0] == "CQ") return "CQ";
            if (IsInvalidCall(words[0])) return null;
            return words[0];
        }

        public static string DeCall(string msg)
        {
            //return the "from" call from the msg in the form "W1AW K1JT FN60" or "W1AW <...> FN60
            //or "CQ K1JT FN60" or "CQ NA K1JT FN60" or "CQ POTA K1JT"
            //but not CQ WY SD K1JT
            //if non-std or invalid msg, return null
            if (IsInvalid(msg)) return null;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() < 2 || words.Count() > 4) return null;
            if (IsCQ(msg))                              //CQ msg
            {
                if (DirectedTo(msg) != null)            //directed CQ msg
                {
                    return words[2];
                }
            }
            if (IsInvalidCall(words[1])) return null;
            return words[1];
        }

        //return payload of message
        //if no payload or non-std or invalid msg, return empty string
        public static string Payload(string msg)
        {
            if (IsInvalid(msg)) return "";
            if (IsCQ(msg))      //CQ considered a payload
            {
                string dirTo = DirectedTo(msg);
                string d = dirTo != null ? $" {dirTo}" : "";
                return $"CQ{d}";
            }
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() <= 2 || words.Count() > 4) return "";
            return words[words.Count() - 1];
        }

        //detect bad (garbage) decode
        public static bool IsInvalidCall(string call)
        {
            if (call.Contains("/")) return false;
            if (call.Length > maxBaseCallsignLength) return true;
            if (IsAlphaOnly(call) || IsNumericOnly(call)) return true;

            //count non-consecutive digits (2-4 consecutive digits can be special event call)
            int n = 0;
            bool prevIsNum = false;
            foreach (char c in call)
            {
                if (c >= '0' && c <= '9')
                {
                    if (!prevIsNum) n++;
                    prevIsNum = true;
                }
                else
                {
                    prevIsNum = false;
                }
            }
            return n >= maxCallDigits;
        }

        public static string RemoveAngleBrackets(string s)
        {
            if (s == null) return null;
            s = s.Replace("<", "");
            s = s.Replace(">", "");
            return s;
        }

        // The exact message cleaning the UDP DecodeMessage/EnqueueDecodeMessage byte parsers
        // have always applied before any shared QSO processing sees the text -- extracted here
        // so the Direct-engine decode path (DirectApplyDecodes) and Direct TxNow can apply the
        // identical normalization instead of feeding raw engine text downstream. Three steps,
        // in the parsers' original order:
        //   1. old AP (a priori) format: "W1AW K1JT FN42        ? a2" -- drop " ?" and after.
        //   2. WSJT-X 3.0 AP suffix: "KI4QMB KE9DMW -15 a35" -> "KI4QMB KE9DMW -15".
        //   3. hashed compound/portable/special-event call: "<W1AW/2> KB0UZT 73" ->
        //      "W1AW/2 KB0UZT 73". An UNRESOLVED hash "<...>" becomes "..." and is still
        //      caught by IsInvalid()'s own Contains("...") check, so this never makes an
        //      unresolved or malformed decode look like a valid callsign.
        public static string NormalizeDecodedMessage(string msg)
        {
            if (msg == null) return null;

            int qIdx = msg.IndexOf(" ?");
            if (qIdx != -1)
                msg = msg.Substring(0, qIdx).TrimEnd();

            {
                int i = msg.Length - 1;
                while (i >= 0 && char.IsDigit(msg[i])) i--;
                if (i < msg.Length - 1 && i >= 1 && msg[i] == 'a' && msg[i - 1] == ' ')
                    msg = msg.Substring(0, i - 1).TrimEnd();
            }

            msg = RemoveAngleBrackets(msg);
            return msg;
        }

        private static bool IsAlphaOnly(string s)
        {
            return !Regex.IsMatch(s, alphaOnly);
        }

        private static bool IsNumericOnly(string s)
        {
            return !Regex.IsMatch(s, numericOnly);
        }

        public static bool IsInvalid(string msg)
        {
            return msg == null || msg.Contains("...") || msg.Contains('<') || msg.Contains('>');
        }

        public static bool IsInvalidType(string msg)
        {
            return !IsCQ(msg) && !IsReply(msg) && !IsShortReply(msg) && !IsReport(msg) && !IsRogerReport(msg) 
                && !IsRogers(msg) && !IsRR73(msg) && !Is73(msg);
        }

        //there are grid codes that *contain* "73", so test for *exactly* "73" or "RR73";
        //msgs in the form "W1AW K1JT 73" or "W1AW K1JT RR73";
        //custom 73 msgs are not acceptable
        public static bool Is73orRR73(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 3) return false;
            return (words[2] == "73" || words[2] == "RR73");
        }

        //there are grid codes that *contain* "73", so test for *exactly* "73";
        //msgs in the form "W1AW K1JT 73";
        //custom 73 msgs are not acceptable
        public static bool Is73(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 3) return false;
            return (words[2] == "73");
        }

        //msgs in the form "W1AW K1JT RR73";
        public static bool IsRR73(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 3) return false;
            return (words[2] == "RR73");
        }
        //msg only in the form "CQ K1JT" or "CQ K1JT EM51" 
        //or "CQ WY K1JT" or "CQ WY K1JT EM51" or "CQ USA K1JT" or "CQ USA K1JT EM51"
        //or "CQ ASIA K1JT EM51" or "CQ POTA K1JT" or "CQ 250 K1JT EM51"
        //but not "CQ WY SD K1JT EM51" or "CQ WY 250 K1JT" (not std msgs)
        public static bool IsCQ(string msg)
        {
            if (ToCall(msg) != "CQ") return false;
            //known to be 2, 3, or 4 words
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            //not "CQ WY SD K1JT" or "CQ USA EUR K1JT" (not std msg)
            if (words.Count() == 4 && (IsAlphaOnly(words[1]) || IsNumericOnly(words[1])) && (IsAlphaOnly(words[2]) || IsAlphaOnly(words[2]))) return false;

            /*foreach (string word in words)
            {
                if (!IsAlphaOnly(word))
                {
                    foreach (string partialPrefix in Prefixes)
                    {
                        if (word.Substring(0, partialPrefix.Length) == partialPrefix)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
            return false;*/

            return true;
        }

        //msg in the form "W1AW K1JT RRR"
        public static bool IsRogers(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 3) return false;
            return (words[2] == "RRR");
        }

        //msg in the form "W1AW K1JT R-03" or "W1AW K1JT R+12"
        public static bool IsRogerReport(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 3) return false;
            if (!words[2].Contains("R+") && !words[2].Contains("R-")) return false;
            if (words[2].Length < 4) return false;
            return (int.TryParse(words[2].Substring(2, 2), out int i));
        }

        //msg in the form "W1AW K1JT -03" or "W1AW K1JT +12"
        public static bool IsReport(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 3) return false;
            if (!words[2].Contains("+") && !words[2].Contains("-")) return false;
            if (words[2].Length != 3) return false;
            return (int.TryParse(words[2].Substring(1, 2), out int i));
        }

        //msg in the form "W1AW K1JT FN62"
        public static bool IsReply(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 3) return false;
            if (IsRogerReport(msg) || IsRogers(msg) || IsCQ(msg) || Is73orRR73(msg)) return false;
            return IsGridFormat(words[2]);
        }

        //msg in the form "W1AW K1JT"
        public static bool IsShortReply(string msg)
        {
            if (IsInvalid(msg)) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() != 2) return false;
            if (IsCQ(msg)) return false;
            return true;
        }

        //similar to: "CQ RU K1JT" or "CQ TEST" or "W1AW K1JT 559 WY" or "W1AW K1JT R 559 WY"
        //or "W1AW K1JT R 559 0002" "W1AW K1JT 569 0021" "W1AW K1JT R DM14"
        //or "WM8Q K9AVT 2A MO" "WM8Q K9AVT R 2A MO"
        public static bool IsContest(string msg)
        {
            if (IsInvalid(msg)) return false;
            //"CQ RU K1JT"
            if (IsCQ(msg))
            {
                string dirTo = DirectedTo(msg);
                return dirTo == "RU" || dirTo == "TEST";
            }
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count() < 4) return false;
            int i;
            //"W1AW K1JT 559 WY"
            //   0   1   2   3
            if (words[2].Length == 3 && int.TryParse(words[2], out i) && words[3].Length == 2) return true;
            //"W1AW K1JT 569 0021"
            //   0   1    2   3
            if (words.Count() == 4 && words[2].Length == 3 && int.TryParse(words[2], out i) && words[3].Length == 4 && int.TryParse(words[3], out i)) return true;
            //"W1AW K1JT R 559 WY"
            //   0   1   2  3  4
            if (words.Count() == 5 && words[2] == "R" && words[3].Length == 3 && int.TryParse(words[3], out i) && words[4].Length == 2) return true;
            //"W1AW K1JT R 559 0002"
            //   0   1   2  3   4
            if (words.Count() == 5 && words[2] == "R" && words[3].Length == 3 && int.TryParse(words[3], out i) && words[4].Length == 4 && int.TryParse(words[4], out i)) return true;
            //W1AW K1JT R DM14
            //   0   1  2  3
            if (words.Count() == 4 && words[2] == "R" && IsGridFormat(words[3])) return true;
            //"WM8Q K9AVT 2A MO"
            //   0   1    2  3
            if (words.Count() == 4 && words[2].Length == 2 && words[3].Length == 2) return true;
            //"WM8Q K9AVT R 2A MO"
            //   0   1    2  3 4
            if (words.Count() == 5 && words[2] == "R" && words[3].Length == 2 && words[4].Length == 2) return true;

            return false;
        }

        public static bool IsPota(string msg)
        {
            //known to be a CQ
            string dirTo = DirectedTo(msg);
            return dirTo != null && (dirTo == "POTA");
        }

        public static bool IsSota(string msg)
        {
            //known to be a CQ
            string dirTo = DirectedTo(msg);
            return dirTo != null && (dirTo == "SOTA");
        }

        // Heuristic: returns true if any word ends with /H.
        // /H is the Hound callsign suffix in Fox/Hound mode, but may also appear
        // in legitimate portable calls. Use SpecialOperationMode from StatusMessage
        // for authoritative Fox/Hound detection. This method signals only
        // "possible Fox/Hound" — never treat the result as definitive.
        public static bool IsFoxHound(string msg)
        {
            if (msg == null) return false;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string word in words)
            {
                if (word.EndsWith("/H", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        //return the "directed to" part of the CQ call (if exists) in a possible CQ msg
        //msg only in the form "CQ WY K1JT" or "CQ WY K1JT EM51" or "CQ USA K1JT" or "CQ CQ K1JT"
        //or "CQ USA K1JT EM51"or "CQ ASIA K1JT EM51" or "CQ POTA K1JT"
        //but not "CQ WY SD K1JT EM51" or "CQ WY SD K1JT" (not std msgs) or "CQ K1JT" 
        //if not a directed CQ msg msg, return null
        public static string DirectedTo(string msg)
        {
            if (IsInvalid(msg)) return null;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            //not "CQ K1JT"
            if (words.Count() < 3 || words.Count() > 4) return null;
            //not K1JT WM8Q DN61
            if (words[0] != "CQ") return null;
            //not "CQ W25 K1JT"
            if (!IsAlphaOnly(words[1]) && !IsNumericOnly(words[1])) return null;
            //nor CQ 250 USA
            if (words.Count() >= 3 && (IsAlphaOnly(words[2]) || IsNumericOnly(words[2]))) return null;
            //not "CQ WY SD K1JT" 
            if (words.Count() == 4 && !IsGridFormat(words[3])) return null;
            return words[1]; 
        }

        //msg in the form "WIAW K2JT +03" or "W1AW K1JT R-04"
        //return RST received from DX station as string (without "R");
        //return null if neither a Report or a RogerReport
        public static string RstRecd(string msg)
        {
            if (IsInvalid(msg)) return null;
            if (!IsReport(msg) && !IsRogerReport(msg)) return null;
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            //already know words.Length and validity of numeric value
            return words[2].Replace("R", "");
        }

        //return the grid from a Reply or CQ
        //null if grid not present
        //ex: "DN61"
        public static string Grid(string msg)
        {
            string[] words = msg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (IsReply(msg))
            {
                return words[2];        //grid format already validated
            }
            if (IsCQ(msg))
            {
                if (words.Length < 3) return null;
                string grid = words[words.Count() - 1];
                if (IsGridFormat(grid)) return grid;
            }
            return null;
        }

        //ex: "DN61"
        public static bool IsGridFormat(string grid)
        {
            if (grid == null || grid.Length != 4) return false;
            int i;
            if (!int.TryParse(grid.Substring(2, 2), out i)) return false;
            if (int.TryParse(grid.Substring(0, 2), out i)) return false;
            return true;
        }

        //return progress in the FT8 QSO protocol (later msgs higher)
        public static int Progress(string msg)
        {
            if (IsCQ(msg)) return 1;
            if (IsReply(msg)) return 2;
            if (IsReport(msg)) return 3;
            if (IsRogerReport(msg)) return 4;
            if (IsRogers(msg)) return 5;
            if (Is73orRR73(msg)) return 6;
            return 0;
        }

        public static void Reinit()
        {
            NegotiatedSchemaVersion = 2;
            NegoState = NegoStates.WAIT;
        }

        private static double RoundToSignificantDigits(double d, int digits)
        {
            if (d == 0)
                return 0;

            double scale = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(d))) + 1);
            return scale * Math.Round(d / scale, digits);
        }
        protected enum Align
        {
            Left, Right
        }

        protected static string Col(object o, int chars, Align alignment)
        {
            if (o == null)
            {
                return new string(' ', chars);
            }

            if (o is double d)
            {
                string str = RoundToSignificantDigits(d, chars - 1).ToString();
                if (!str.Contains("."))
                {
                    str += ".0";
                }
                return Col(str, chars, alignment);
            }

            string output = o.ToString();
            if (o is bool)
            {
                output = output.Substring(0, 1);
            }

            if (output.Length > chars)
            {
                if (alignment == Align.Left)
                {
                    return output.Substring(0, chars);
                }
                else
                {
                    return output.Substring(output.Length - chars, chars);
                }
            }
            else if (output.Length == chars)
            {
                return output;
            }
            else
            {
                if (alignment == Align.Left)
                {
                    return output + new string(' ', chars - output.Length);
                }
                else
                {
                    return new string(' ', chars - output.Length) + output;
                }
            }
        }
    }
}
