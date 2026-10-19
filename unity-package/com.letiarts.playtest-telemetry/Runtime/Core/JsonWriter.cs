using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Minimal allocation-conscious JSON writer (JsonUtility cannot serialize dictionaries and we do
    /// not depend on Newtonsoft). Not thread-safe; reuse one instance per thread via <see cref="Clear"/>.
    /// </summary>
    internal sealed class JsonWriter
    {
        const int MaxDepth = 8;

        readonly StringBuilder sb;
        // One entry per open container: true once the container has at least one element.
        readonly List<bool> hasElement = new List<bool>(8);
        bool afterName;

        public JsonWriter(int capacity = 1024)
        {
            sb = new StringBuilder(capacity);
        }

        public int Length => sb.Length;

        public void Clear()
        {
            sb.Length = 0;
            hasElement.Clear();
            afterName = false;
        }

        public override string ToString()
        {
            return sb.ToString();
        }

        void BeforeValue()
        {
            if (afterName)
            {
                afterName = false;
                return;
            }
            int last = hasElement.Count - 1;
            if (last >= 0)
            {
                if (hasElement[last]) sb.Append(',');
                hasElement[last] = true;
            }
        }

        public JsonWriter BeginObject()
        {
            BeforeValue();
            sb.Append('{');
            hasElement.Add(false);
            return this;
        }

        public JsonWriter EndObject()
        {
            sb.Append('}');
            hasElement.RemoveAt(hasElement.Count - 1);
            return this;
        }

        public JsonWriter BeginArray()
        {
            BeforeValue();
            sb.Append('[');
            hasElement.Add(false);
            return this;
        }

        public JsonWriter EndArray()
        {
            sb.Append(']');
            hasElement.RemoveAt(hasElement.Count - 1);
            return this;
        }

        public JsonWriter Name(string name)
        {
            int last = hasElement.Count - 1;
            if (hasElement[last]) sb.Append(',');
            hasElement[last] = true;
            WriteString(name);
            sb.Append(':');
            afterName = true;
            return this;
        }

        public JsonWriter Value(string value)
        {
            BeforeValue();
            if (value == null) sb.Append("null");
            else WriteString(value);
            return this;
        }

        public JsonWriter Value(long value)
        {
            BeforeValue();
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(double value)
        {
            BeforeValue();
            if (double.IsNaN(value) || double.IsInfinity(value)) sb.Append("null");
            else sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Value(bool value)
        {
            BeforeValue();
            sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonWriter Null()
        {
            BeforeValue();
            sb.Append("null");
            return this;
        }

        /// <summary>Appends an already-serialized JSON value verbatim.</summary>
        public JsonWriter Raw(string json)
        {
            BeforeValue();
            sb.Append(string.IsNullOrEmpty(json) ? "null" : json);
            return this;
        }

        // Convenience: "name": value
        public JsonWriter Prop(string name, string value) { return Name(name).Value(value); }
        public JsonWriter Prop(string name, long value) { return Name(name).Value(value); }
        public JsonWriter Prop(string name, double value) { return Name(name).Value(value); }
        public JsonWriter Prop(string name, bool value) { return Name(name).Value(value); }
        public JsonWriter PropRaw(string name, string json) { return Name(name).Raw(json); }

        /// <summary>Writes an arbitrary object: primitives, strings, enums, dictionaries, enumerables.</summary>
        public JsonWriter Value(object value)
        {
            WriteObject(value, 0);
            return this;
        }

        void WriteObject(object value, int depth)
        {
            switch (value)
            {
                case null:
                    Null();
                    return;
                case string s:
                    Value(s);
                    return;
                case bool b:
                    Value(b);
                    return;
                case int i:
                    Value((long)i);
                    return;
                case long l:
                    Value(l);
                    return;
                case short sh:
                    Value((long)sh);
                    return;
                case byte by:
                    Value((long)by);
                    return;
                case uint ui:
                    Value((long)ui);
                    return;
                case ulong ul:
                    BeforeValue();
                    sb.Append(ul.ToString(CultureInfo.InvariantCulture));
                    return;
                case float f:
                    Value((double)f);
                    return;
                case double d:
                    Value(d);
                    return;
                case decimal m:
                    BeforeValue();
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case char c:
                    Value(c.ToString());
                    return;
                case Enum e:
                    Value(e.ToString());
                    return;
                case DateTime dt:
                    Value(dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    return;
            }

            if (depth >= MaxDepth)
            {
                Value(value.ToString());
                return;
            }

            if (value is IDictionary<string, object> typed)
            {
                BeginObject();
                foreach (KeyValuePair<string, object> entry in typed)
                {
                    Name(entry.Key ?? "null");
                    WriteObject(entry.Value, depth + 1);
                }
                EndObject();
                return;
            }

            if (value is IDictionary dict)
            {
                BeginObject();
                foreach (DictionaryEntry entry in dict)
                {
                    Name(entry.Key == null ? "null" : entry.Key.ToString());
                    WriteObject(entry.Value, depth + 1);
                }
                EndObject();
                return;
            }

            if (value is IEnumerable seq)
            {
                BeginArray();
                foreach (object item in seq) WriteObject(item, depth + 1);
                EndArray();
                return;
            }

            Value(value.ToString());
        }

        void WriteString(string s)
        {
            sb.Append('"');
            int runStart = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string escape = null;
                if (c == '"') escape = "\\\"";
                else if (c == '\\') escape = "\\\\";
                else if (c < 0x20)
                {
                    switch (c)
                    {
                        case '\n': escape = "\\n"; break;
                        case '\r': escape = "\\r"; break;
                        case '\t': escape = "\\t"; break;
                        case '\b': escape = "\\b"; break;
                        case '\f': escape = "\\f"; break;
                        default: escape = "\\u" + ((int)c).ToString("x4"); break;
                    }
                }
                else if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        i++; // valid pair: written as-is with the run
                        continue;
                    }
                    escape = "\\u" + ((int)c).ToString("x4"); // lone surrogate would break UTF-8 encoding
                }
                else if (char.IsLowSurrogate(c))
                {
                    escape = "\\u" + ((int)c).ToString("x4");
                }

                if (escape != null)
                {
                    if (i > runStart) sb.Append(s, runStart, i - runStart);
                    sb.Append(escape);
                    runStart = i + 1;
                }
            }
            if (s.Length > runStart) sb.Append(s, runStart, s.Length - runStart);
            sb.Append('"');
        }
    }
}
