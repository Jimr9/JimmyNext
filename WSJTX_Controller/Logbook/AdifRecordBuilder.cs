using System.Globalization;
using System.Text;

namespace WSJTX_Controller
{
    // Builds a single ADIF QSO record (one <eor>-terminated string), shared by
    // both the QRZ and Club Log upload paths so the field list only lives in one
    // place. Not used for local LogbookDb import -- that goes through the raw
    // field dictionary + AdifImporter/AdifParser pipeline instead.
    public static class AdifRecordBuilder
    {
        public static string Build(
            string call, string band, long freqHz, string mode,
            string qsoDate, string timeOn, string timeOff,
            string rstSent, string rstRcvd, string grid, string name, string comment,
            string txPwr, string operatorCall, string stationCall, string myGrid,
            string exchangeSent = "", string exchangeRcvd = "", string qsoDateOff = "",
            string state = "", string sig = "", string sigInfo = "")
        {
            var sb = new StringBuilder();
            void F(string field, string val)
            {
                if (!string.IsNullOrEmpty(val))
                    sb.Append('<').Append(field).Append(':').Append(val.Length).Append('>').Append(val);
            }

            F("call", call);
            F("band", band);
            if (freqHz > 0)
                F("freq", (freqHz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture));
            F("mode", mode);
            F("qso_date", qsoDate);
            F("time_on", timeOn);
            F("qso_date_off", qsoDateOff);
            F("time_off", timeOff);
            F("rst_sent", rstSent);
            F("rst_rcvd", rstRcvd);
            F("gridsquare", grid);
            F("state", state);
            F("name", name);
            F("comment", comment);
            F("tx_pwr", txPwr);
            F("operator", operatorCall);
            F("station_callsign", stationCall);
            F("my_gridsquare", myGrid);
            F("stx_string", exchangeSent);
            F("srx_string", exchangeRcvd);
            F("sig", sig);
            F("sig_info", sigInfo);
            sb.Append("<eor>\r\n");
            return sb.ToString();
        }
    }
}
