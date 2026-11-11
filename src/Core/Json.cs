using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
