using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace AiUsageWidget.Core
{
    // Thin helpers over JavaScriptSerializer: JSON in/out plus safe accessors for the
    // Dictionary<string, object> graphs the serializer produces.
    public static class Json
    {
        public static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return NewSerializer().Deserialize<Dictionary<string, object>>(text);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static string Serialize(object value)
        {
            return NewSerializer().Serialize(value);
        }

        // Minified serialize, then a pretty-printer pass. The printer tracks string state,
        // so quotes, backslashes, braces, brackets, commas and colons inside strings never
        // break the layout; the output is still valid JSON and parses back unchanged.
        public static string SerializeIndented(object value)
        {
            return Indent(Serialize(value));
        }

        static string Indent(string json)
        {
            if (json == null) return null;
            var sb = new StringBuilder(json.Length + 32);
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\')
                    {
                        int escaped = i + 1;
                        if (escaped < json.Length)
                        {
                            sb.Append(json[escaped]);   // verbatim, never interpreted
                            i = escaped;
                        }
                    }
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                    {
                        sb.Append(c);
                        char close = c == '{' ? '}' : ']';
                        int next = NextNonSpace(json, i + 1);
                        if (next >= 0 && json[next] == close)
                        {
                            sb.Append(close);   // {} and [] stay on one line
                            i = next;
                        }
                        else
                        {
                            depth++;
                            NewLine(sb, depth);
                        }
                        break;
                    }
                    case '}':
                    case ']':
                    {
                        depth--;
                        NewLine(sb, depth);
                        sb.Append(c);
                        break;
                    }
                    case ',':
                        sb.Append(c);
                        NewLine(sb, depth);
                        break;
                    case ':':
                        sb.Append(c);
                        sb.Append(' ');
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static int NextNonSpace(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
                if (s[i] != ' ') return i;
            return -1;
        }

        static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append(Environment.NewLine);
            sb.Append(' ', depth * 2);
        }

        static JavaScriptSerializer NewSerializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 256;
            return s;
        }

        public static object Get(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object value;
            if (!dict.TryGetValue(key, out value)) return null;
            return value;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> dict, string key)
        {
            return Get(dict, key) as Dictionary<string, object>;
        }

        public static List<object> GetArray(Dictionary<string, object> dict, string key)
        {
            object value = Get(dict, key);
            if (value == null) return null;
            var list = new List<object>();
            var arr = value as object[];
            if (arr != null)
            {
                list.AddRange(arr);
                return list;
            }
            var seq = value as IEnumerable;
            if (seq != null)
            {
                foreach (object item in seq) list.Add(item);
                return list;
            }
            return null;
        }

        public static string GetStr(Dictionary<string, object> dict, string key)
        {
            return Get(dict, key) as string;
        }

        public static bool GetBool(Dictionary<string, object> dict, string key, bool fallback)
        {
            object value = Get(dict, key);
            if (value is bool) return (bool)value;
            return fallback;
        }

        public static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            object value = Get(dict, key);
            if (value == null || value is bool) return null;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (InvalidCastException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        public static int GetInt(Dictionary<string, object> dict, string key, int fallback)
        {
            double? value = GetDouble(dict, key);
            if (!value.HasValue) return fallback;
            return (int)Math.Round(value.Value);
        }
    }
}
