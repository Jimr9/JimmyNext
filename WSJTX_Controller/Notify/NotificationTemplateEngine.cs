using System.Collections.Generic;
using System.Text;

namespace WSJTX_Controller
{
    // Simple {Token}-substitution -- deliberately not a scripting/expression language. A linear
    // scan, not regex: no escaping, no nesting, no conditionals. Unknown tokens are left in the
    // output verbatim rather than dropped or thrown -- an operator sees an obviously-wrong
    // "{Typo}" in speech and reports it, which is safer than a silently-mangled sentence or a
    // crash. Pure/static so JimmyTests can exercise it directly, matching RowFormatter.cs's
    // testability shape.
    public static class NotificationTemplateEngine
    {
        public static string Format(string template, IReadOnlyDictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(template)) return "";

            var sb = new StringBuilder(template.Length);
            int i = 0;
            while (i < template.Length)
            {
                if (template[i] == '{')
                {
                    int close = template.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        string name = template.Substring(i + 1, close - i - 1);
                        if (tokens != null && tokens.TryGetValue(name, out string val))
                            sb.Append(val ?? "");
                        else
                            sb.Append(template, i, close - i + 1);   // unknown/missing token: literal, never throws
                        i = close + 1;
                        continue;
                    }
                }
                sb.Append(template[i]);
                i++;
            }
            return sb.ToString();
        }

        // Singular/plural belongs in formatter code, not in INI templates -- keeps templates
        // simple ("no scripting language") while still producing "1 award needed" / "2 awards
        // needed" correctly. plural defaults to singular + "s" when the caller has nothing more
        // specific to say (matches the common case; irregular plurals should pass their own).
        public static string Pluralize(int count, string singular, string plural = null)
        {
            string word = count == 1 ? singular : (plural ?? singular + "s");
            return $"{count} {word}";
        }
    }
}
