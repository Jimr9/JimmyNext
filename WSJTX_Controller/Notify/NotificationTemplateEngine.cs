using System.Collections.Generic;
using System.Text;

namespace WSJTX_Controller
{
    // One component of a parsed template: either a run of literal text, or a {Variable}
    // reference (Text holds the bare key name, no braces). The configurable-notification UI's
    // checklist/reorder controls operate on these, not on the raw string, per the design
    // decision that the template STRING stays the single persisted source of truth -- the
    // component list is always re-derived from it, never stored separately (see
    // NotificationSettings: only Template itself is persisted).
    public readonly struct TemplateComponent
    {
        public bool IsVariable { get; }
        public string Text { get; }

        private TemplateComponent(bool isVariable, string text)
        {
            IsVariable = isVariable;
            Text = text;
        }

        public static TemplateComponent Literal(string text) => new TemplateComponent(false, text);
        public static TemplateComponent Variable(string key) => new TemplateComponent(true, key);
    }

    // Simple {Token}-substitution -- deliberately not a scripting/expression language. A linear
    // scan, not regex: no escaping, no nesting, no conditionals. Unknown tokens are left in the
    // output verbatim rather than dropped or thrown -- an operator sees an obviously-wrong
    // "{Typo}" in speech and reports it, which is safer than a silently-mangled sentence or a
    // crash. Pure/static so JimmyTests can exercise it directly, matching RowFormatter.cs's
    // testability shape.
    public static class NotificationTemplateEngine
    {
        // The one brace-scanner every other method here (and NotificationVariableRegistry's
        // validator, and the config UI's checklist) builds on -- "what counts as a token" is
        // defined in exactly this one place. Malformed braces (an unclosed '{') fall back to
        // literal text, same tolerant rule this class has always used.
        public static List<TemplateComponent> ParseComponents(string template)
        {
            var result = new List<TemplateComponent>();
            if (string.IsNullOrEmpty(template)) return result;

            var literal = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                if (template[i] == '{')
                {
                    int close = template.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        if (literal.Length > 0)
                        {
                            result.Add(TemplateComponent.Literal(literal.ToString()));
                            literal.Clear();
                        }
                        result.Add(TemplateComponent.Variable(template.Substring(i + 1, close - i - 1)));
                        i = close + 1;
                        continue;
                    }
                }
                literal.Append(template[i]);
                i++;
            }
            if (literal.Length > 0) result.Add(TemplateComponent.Literal(literal.ToString()));
            return result;
        }

        public static string Format(string template, IReadOnlyDictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(template)) return "";

            var sb = new StringBuilder(template.Length);
            foreach (var c in ParseComponents(template))
            {
                if (!c.IsVariable)
                {
                    sb.Append(c.Text);
                    continue;
                }
                if (tokens != null && tokens.TryGetValue(c.Text, out string val))
                    sb.Append(val ?? "");
                else
                    sb.Append('{').Append(c.Text).Append('}');   // unknown/missing token: literal, never throws
            }
            return sb.ToString();
        }

        // Ordered, de-duplicated variable keys referenced by a template -- backs
        // NotificationVariableRegistry.Validate and the config UI's checklist-from-text sync.
        public static List<string> ExtractVariableNames(string template)
        {
            var seen = new HashSet<string>();
            var names = new List<string>();
            foreach (var c in ParseComponents(template))
            {
                if (!c.IsVariable || !seen.Add(c.Text)) continue;
                names.Add(c.Text);
            }
            return names;
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
