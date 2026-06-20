using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YuraSub.Native.Json;

/// <summary>
/// Minimal AOT-friendly JSON value types and parser/writer.
/// No reflection, no System.Text.Json dependency.
/// </summary>

internal abstract class JsonValue
{
    public abstract override string ToString();

    // Implicit conversions from C# types to JsonValue
    public static implicit operator JsonValue(string value) => new JsonString(value);
    public static implicit operator JsonValue(int value) => new JsonNumber(value);
    public static implicit operator JsonValue(long value) => new JsonNumber(value);
    public static implicit operator JsonValue(double value) => new JsonNumber(value);
    public static implicit operator JsonValue(float value) => new JsonNumber(value);
    public static implicit operator JsonValue(bool value) => new JsonBool(value);
}

internal sealed class JsonNull : JsonValue
{
    public static readonly JsonNull Instance = new();
    public override string ToString() => "null";
}

internal sealed class JsonBool : JsonValue
{
    public bool Value { get; }
    public JsonBool(bool value) => Value = value;
    public override string ToString() => Value ? "true" : "false";
}

internal sealed class JsonNumber : JsonValue
{
    public double Value { get; }
    public JsonNumber(double value) => Value = value;
    public override string ToString()
    {
        if (Value == Math.Floor(Value) && Math.Abs(Value) < 1e15)
            return ((long)Value).ToString(CultureInfo.InvariantCulture);
        return Value.ToString("G", CultureInfo.InvariantCulture);
    }
    public long ToLong() => (long)Value;
    public int ToInt() => (int)Value;
}

internal sealed class JsonString : JsonValue
{
    public string Value { get; }
    public JsonString(string value) => Value = value;
    public override string ToString() => Value;
}

internal sealed class JsonArray : JsonValue, IEnumerable<JsonValue>
{
    private readonly List<JsonValue> _items = new();
    public int Count => _items.Count;
    public JsonValue this[int index] => _items[index];
    public void Add(JsonValue item) => _items.Add(item);
    public IEnumerator<JsonValue> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => $"[...{_items.Count} items]";
}

internal sealed class JsonObject : JsonValue, IEnumerable<KeyValuePair<string, JsonValue>>
{
    private readonly Dictionary<string, JsonValue> _dict = new(StringComparer.Ordinal);
    public int Count => _dict.Count;
    public JsonValue this[string key]
    {
        get => _dict[key];
        set => _dict[key] = value;
    }
    public bool TryGetValue(string key, out JsonValue value) => _dict.TryGetValue(key, out value!);
    public bool ContainsKey(string key) => _dict.ContainsKey(key);
    public void Remove(string key) => _dict.Remove(key);
    public IEnumerable<string> Keys => _dict.Keys;
    public IEnumerable<JsonValue> Values => _dict.Values;
    public void Clear() => _dict.Clear();
    public IEnumerator<KeyValuePair<string, JsonValue>> GetEnumerator() => _dict.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => $"{{...{_dict.Count} keys}}";
}

/// <summary>
/// Simple JSON parser.
/// </summary>
internal static class JsonSimple
{
    public static JsonValue Parse(string text)
    {
        int pos = 0;
        var result = ParseValue(text, ref pos);
        SkipWhitespace(text, ref pos);
        return result;
    }

    private static JsonValue ParseValue(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) throw new FormatException("Unexpected end of JSON");
        char c = text[pos];
        if (c == '"') return ParseString(text, ref pos);
        if (c == '{') return ParseObject(text, ref pos);
        if (c == '[') return ParseArray(text, ref pos);
        if (c == 't' || c == 'f') return ParseBool(text, ref pos);
        if (c == 'n') return ParseNull(text, ref pos);
        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(text, ref pos);
        throw new FormatException($"Unexpected character '{c}' at position {pos}");
    }

    private static JsonString ParseString(string text, ref int pos)
    {
        pos++; // skip opening "
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos++];
            if (c == '"') return new JsonString(sb.ToString());
            if (c == '\\')
            {
                if (pos >= text.Length) break;
                char esc = text[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 <= text.Length)
                        {
                            string hex = text.Substring(pos, 4);
                            sb.Append((char)Convert.ToInt32(hex, 16));
                            pos += 4;
                        }
                        break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        throw new FormatException("Unterminated string");
    }

    private static JsonObject ParseObject(string text, ref int pos)
    {
        pos++; // skip {
        SkipWhitespace(text, ref pos);
        var obj = new JsonObject();
        if (pos < text.Length && text[pos] == '}') { pos++; return obj; }
        while (pos < text.Length)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length || text[pos] != '"') throw new FormatException("Expected string key");
            var key = ParseString(text, ref pos).Value;
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length || text[pos] != ':') throw new FormatException("Expected ':'");
            pos++;
            obj[key] = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) break;
            if (text[pos] == '}') { pos++; return obj; }
            if (text[pos] == ',') { pos++; continue; }
            throw new FormatException("Expected ',' or '}'");
        }
        throw new FormatException("Unterminated object");
    }

    private static JsonArray ParseArray(string text, ref int pos)
    {
        pos++; // skip [
        SkipWhitespace(text, ref pos);
        var arr = new JsonArray();
        if (pos < text.Length && text[pos] == ']') { pos++; return arr; }
        while (pos < text.Length)
        {
            arr.Add(ParseValue(text, ref pos));
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) break;
            if (text[pos] == ']') { pos++; return arr; }
            if (text[pos] == ',') { pos++; continue; }
            throw new FormatException("Expected ',' or ']'");
        }
        throw new FormatException("Unterminated array");
    }

    private static JsonNumber ParseNumber(string text, ref int pos)
    {
        int start = pos;
        if (pos < text.Length && text[pos] == '-') pos++;
        while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') pos++;
        if (pos < text.Length && text[pos] == '.')
        {
            pos++;
            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') pos++;
        }
        if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E'))
        {
            pos++;
            if (pos < text.Length && (text[pos] == '+' || text[pos] == '-')) pos++;
            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') pos++;
        }
        string numStr = text.Substring(start, pos - start);
        return new JsonNumber(double.Parse(numStr, CultureInfo.InvariantCulture));
    }

    private static JsonBool ParseBool(string text, ref int pos)
    {
        if (text.Substring(pos, 4) == "true") { pos += 4; return new JsonBool(true); }
        if (text.Substring(pos, 5) == "false") { pos += 5; return new JsonBool(false); }
        throw new FormatException("Invalid boolean");
    }

    private static JsonNull ParseNull(string text, ref int pos)
    {
        if (text.Substring(pos, 4) == "null") { pos += 4; return JsonNull.Instance; }
        throw new FormatException("Invalid null");
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r' || text[pos] == '\n'))
            pos++;
    }

    // --- Stringify ---

    public static string Stringify(JsonValue value)
    {
        var sb = new StringBuilder();
        StringifyValue(sb, value);
        return sb.ToString();
    }

    private static void StringifyValue(StringBuilder sb, JsonValue value)
    {
        switch (value)
        {
            case JsonNull: sb.Append("null"); break;
            case JsonBool b: sb.Append(b.Value ? "true" : "false"); break;
            case JsonNumber n:
                if (n.Value == Math.Floor(n.Value) && Math.Abs(n.Value) < 1e15)
                    sb.Append(((long)n.Value).ToString(CultureInfo.InvariantCulture));
                else
                    sb.Append(n.Value.ToString("G", CultureInfo.InvariantCulture));
                break;
            case JsonString s:
                sb.Append('"');
                foreach (char c in s.Value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                sb.Append($"\\u{(int)c:x4}");
                            else
                                sb.Append(c); // Allow non-ASCII unescaped (ensure_ascii=False equivalent)
                            break;
                    }
                }
                sb.Append('"');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    StringifyValue(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (var kv in obj)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"');
                    foreach (char c in kv.Key)
                    {
                        if (c == '"') sb.Append("\\\"");
                        else if (c == '\\') sb.Append("\\\\");
                        else if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                    }
                    sb.Append('"');
                    sb.Append(':');
                    StringifyValue(sb, kv.Value);
                }
                sb.Append('}');
                break;
        }
    }
}
