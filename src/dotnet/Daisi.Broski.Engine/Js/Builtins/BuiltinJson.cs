using System.Globalization;
using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>JSON</c> — a single global object with <c>parse</c> and
/// <c>stringify</c> methods. Neither is a constructor. Both are
/// implemented as straightforward recursive walkers since JSON is
/// much simpler than JavaScript itself.
///
/// Deliberately deferred for slice 6c:
///
/// - The <c>reviver</c> argument to <c>JSON.parse</c> and the
///   <c>replacer</c> argument to <c>JSON.stringify</c>. Both are
///   callback-taking, so they would need the re-entrant VM path
///   established in slice 6b. Deferring until a site actually
///   uses them — most <c>JSON.stringify(value)</c> calls pass
///   neither.
/// - Indentation / pretty-printing: the <c>space</c> argument to
///   <c>stringify</c>. We always produce compact output.
/// - <c>toJSON</c> method dispatch on objects.
/// </summary>
internal static class BuiltinJson
{
    public static void Install(JsEngine engine)
    {
        var json = new JsObject { Prototype = engine.ObjectPrototype };
        Builtins.Method(json, "parse", (t, a) => Parse(engine, a));
        Builtins.Method(json, "stringify", (t, a) => Stringify(a));
        engine.Globals["JSON"] = json;
    }

    // -------------------------------------------------------------------
    // JSON.parse
    // -------------------------------------------------------------------

    private static object? Parse(JsEngine engine, IReadOnlyList<object?> args)
    {
        var text = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
        return ParseText(engine, text);
    }

    /// <summary>
    /// Helper used by other native built-ins (e.g.
    /// <c>Response.prototype.json</c>) that need to parse
    /// a JSON string into a JS value without routing
    /// through the script-visible <c>JSON.parse</c>.
    /// </summary>
    internal static object? ParseText(JsEngine engine, string text)
    {
        var parser = new JsonParser(text, engine);
        return parser.ParseRoot();
    }

    private sealed class JsonParser
    {
        private readonly string _src;
        private readonly JsEngine _engine;
        private int _pos;

        public JsonParser(string src, JsEngine engine)
        {
            _src = src;
            _engine = engine;
        }

        public object? ParseRoot()
        {
            SkipWs();
            var v = ParseValue();
            SkipWs();
            if (_pos < _src.Length)
            {
                Fail("Unexpected trailing characters");
            }
            return v;
        }

        private object? ParseValue()
        {
            SkipWs();
            if (_pos >= _src.Length) Fail("Unexpected end of JSON input");

            char c = _src[_pos];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return ParseString();
                case 't':
                case 'f':
                case 'n': return ParseKeyword();
                case '-':
                    return ParseNumber();
            }
            if (c >= '0' && c <= '9') return ParseNumber();

            Fail($"Unexpected character '{c}'");
            return null; // unreachable
        }

        private JsObject ParseObject()
        {
            Expect('{');
            var obj = new JsObject { Prototype = _engine.ObjectPrototype };
            SkipWs();
            if (Peek() == '}')
            {
                _pos++;
                return obj;
            }

            while (true)
            {
                SkipWs();
                if (_pos >= _src.Length || _src[_pos] != '"')
                {
                    Fail("Expected string key in object");
                }
                var key = ParseString();
                SkipWs();
                Expect(':');
                var value = ParseValue();
                obj.Set(key, value);
                SkipWs();
                if (Peek() == ',')
                {
                    _pos++;
                    continue;
                }
                if (Peek() == '}')
                {
                    _pos++;
                    return obj;
                }
                Fail("Expected ',' or '}' in object");
            }
        }

        private JsArray ParseArray()
        {
            Expect('[');
            var arr = new JsArray { Prototype = _engine.ArrayPrototype };
            SkipWs();
            if (Peek() == ']')
            {
                _pos++;
                return arr;
            }

            while (true)
            {
                var value = ParseValue();
                arr.Elements.Add(value);
                SkipWs();
                if (Peek() == ',')
                {
                    _pos++;
                    continue;
                }
                if (Peek() == ']')
                {
                    _pos++;
                    return arr;
                }
                Fail("Expected ',' or ']' in array");
            }
        }

        private string ParseString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '"')
                {
                    _pos++;
                    return sb.ToString();
                }
                if (c < 0x20) Fail($"Unescaped control character in string (0x{(int)c:X})");
                if (c == '\\')
                {
                    _pos++;
                    if (_pos >= _src.Length) Fail("Unexpected end in escape");
                    char esc = _src[_pos++];
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
                            if (_pos + 4 > _src.Length) Fail("Bad \\u escape");
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int d = HexDigit(_src[_pos + i]);
                                if (d < 0) Fail("Bad \\u escape digit");
                                code = (code << 4) | d;
                            }
                            _pos += 4;
                            sb.Append((char)code);
                            break;
                        default:
                            Fail($"Unknown escape '\\{esc}'");
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                    _pos++;
                }
            }
            Fail("Unterminated string");
            return "";
        }

        private double ParseNumber()
        {
            int start = _pos;
            if (_src[_pos] == '-') _pos++;

            // Int part.
            if (_pos >= _src.Length) Fail("Unexpected end in number");
            if (_src[_pos] == '0')
            {
                _pos++;
            }
            else if (_src[_pos] >= '1' && _src[_pos] <= '9')
            {
                while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9') _pos++;
            }
            else
            {
                Fail("Invalid number");
            }

            // Fraction.
            if (_pos < _src.Length && _src[_pos] == '.')
            {
                _pos++;
                int fracStart = _pos;
                while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9') _pos++;
                if (_pos == fracStart) Fail("Expected digit after '.'");
            }

            // Exponent.
            if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
                int expStart = _pos;
                while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9') _pos++;
                if (_pos == expStart) Fail("Expected digit in exponent");
            }

            var text = _src.Substring(start, _pos - start);
            if (!double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                Fail("Invalid number");
            }
            return value;
        }

        private object? ParseKeyword()
        {
            if (Matches("true")) { _pos += 4; return JsValue.True; }
            if (Matches("false")) { _pos += 5; return JsValue.False; }
            if (Matches("null")) { _pos += 4; return JsValue.Null; }
            Fail("Unknown literal");
            return null;
        }

        // -------- low-level helpers --------

        private void SkipWs()
        {
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _pos++;
                else break;
            }
        }

        private char Peek() => _pos < _src.Length ? _src[_pos] : '\0';

        private void Expect(char c)
        {
            if (_pos >= _src.Length || _src[_pos] != c)
            {
                Fail($"Expected '{c}'");
            }
            _pos++;
        }

        private bool Matches(string s)
        {
            if (_pos + s.Length > _src.Length) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (_src[_pos + i] != s[i]) return false;
            }
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private void Fail(string message)
        {
            JsThrow.SyntaxError($"JSON.parse: {message} at position {_pos}");
        }
    }

    // -------------------------------------------------------------------
    // JSON.stringify
    // -------------------------------------------------------------------

    /// <summary>
    /// Serialize a JS value to a JSON string. Ignores the
    /// <c>replacer</c> and <c>space</c> arguments (slice-6c
    /// simplification). Functions and <c>undefined</c> are
    /// omitted from object output and rendered as <c>null</c>
    /// when they appear in array slots, per ECMA §15.12.3.
    /// Throws <c>TypeError</c> on object cycles.
    /// </summary>
    private static object? Stringify(IReadOnlyList<object?> args)
    {
        if (args.Count == 0) return JsValue.Undefined;
        var value = args[0];
        var sb = new StringBuilder();
        var seen = new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        if (!StringifyValue(value, sb, seen))
        {
            // Top-level value was a function or undefined —
            // JSON.stringify returns undefined in that case.
            return JsValue.Undefined;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Append a JSON rendering of <paramref name="value"/> to
    /// <paramref name="sb"/>. Returns false if the value is
    /// something that JSON.stringify skips entirely (function,
    /// undefined, symbol), which the caller handles.
    /// </summary>
    private static bool StringifyValue(object? value, StringBuilder sb, HashSet<JsObject> seen)
    {
        if (value is JsUndefined || value is JsFunction) return false;
        if (value is JsNull || value is null)
        {
            sb.Append("null");
            return true;
        }
        if (value is bool b)
        {
            sb.Append(b ? "true" : "false");
            return true;
        }
        if (value is double d)
        {
            // Per spec: NaN / Infinity / -Infinity serialize to "null".
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
            }
            else
            {
                sb.Append(JsValue.ToJsString(d));
            }
            return true;
        }
        if (value is string s)
        {
            AppendQuotedString(s, sb);
            return true;
        }
        if (value is JsDate date)
        {
            // Match the effect of `Date.prototype.toJSON`
            // without going through the VM — Date is the only
            // built-in that customizes `toJSON`, and handling
            // it inline lets JSON.stringify stay a
            // VM-independent native method. Invalid dates
            // render as `null`, matching browsers.
            if (!date.IsValid)
            {
                sb.Append("null");
                return true;
            }
            AppendQuotedString(BuiltinDate.FormatIso(date.Time), sb);
            return true;
        }
        if (value is JsArray arr)
        {
            if (!seen.Add(arr))
            {
                JsThrow.TypeError("Converting circular structure to JSON");
            }
            sb.Append('[');
            for (int i = 0; i < arr.Elements.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var element = arr.Elements[i];
                if (!StringifyValue(element, sb, seen))
                {
                    // Functions / undefined in an array slot
                    // render as null (per spec §15.12.3).
                    sb.Append("null");
                }
            }
            sb.Append(']');
            seen.Remove(arr);
            return true;
        }
        if (value is JsObject obj)
        {
            if (!seen.Add(obj))
            {
                JsThrow.TypeError("Converting circular structure to JSON");
            }
            sb.Append('{');
            bool first = true;
            foreach (var key in obj.OwnKeys())
            {
                var propVal = obj.Get(key);
                // Probe without writing: we need to detect
                // skippable values before emitting the key +
                // colon so we don't leave a dangling trailing
                // comma. Use a throwaway StringBuilder.
                var tmp = new StringBuilder();
                if (!StringifyValue(propVal, tmp, seen))
                {
                    continue;
                }
                if (!first) sb.Append(',');
                first = false;
                AppendQuotedString(key, sb);
                sb.Append(':');
                sb.Append(tmp);
            }
            sb.Append('}');
            seen.Remove(obj);
            return true;
        }
        // Anything else (should be unreachable) → null.
        sb.Append("null");
        return true;
    }

    /// <summary>
    /// Append a JSON-quoted string literal. Handles the standard
    /// escapes (<c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\r</c>,
    /// <c>\t</c>, <c>\b</c>, <c>\f</c>) and emits <c>\uXXXX</c>
    /// for any remaining control character.
    /// </summary>
    private static void AppendQuotedString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in s)
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
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
