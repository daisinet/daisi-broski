using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Binary-to-base64 <c>btoa</c> and the reverse <c>atob</c>,
/// the two globals HTML provides for base64 conversion. Both
/// operate on the historical "binary string" convention: one
/// JS character per byte, where characters outside
/// <c>0x00–0xFF</c> throw <c>InvalidCharacterError</c> (which
/// we surface as a plain <c>TypeError</c> — no DOMException
/// hierarchy yet).
///
/// <para>
/// For encoding arbitrary Unicode strings, the canonical
/// pattern is <c>btoa(unescape(encodeURIComponent(str)))</c>
/// on the write side and <c>decodeURIComponent(escape(atob(b)))</c>
/// on the read side — these are two separate layers on top
/// of the same base64 step.
/// </para>
/// </summary>
internal static class BuiltinBase64
{
    public static void Install(JsEngine engine)
    {
        engine.Globals["btoa"] = new JsFunction("btoa", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is JsUndefined)
            {
                JsThrow.TypeError("btoa: argument is required");
            }
            var input = JsValue.ToJsString(args[0]);
            var bytes = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c > 0xFF)
                {
                    JsThrow.TypeError(
                        "btoa: string contains characters outside of the Latin1 range");
                }
                bytes[i] = (byte)c;
            }
            return Convert.ToBase64String(bytes);
        });

        engine.Globals["atob"] = new JsFunction("atob", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is JsUndefined)
            {
                JsThrow.TypeError("atob: argument is required");
            }
            var input = JsValue.ToJsString(args[0]);
            // Spec says to remove any ASCII whitespace before
            // decoding (browsers are liberal about pasted
            // line-wrapped base64).
            var cleaned = StripAsciiWhitespace(input);
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(cleaned);
            }
            catch (FormatException)
            {
                JsThrow.TypeError("atob: invalid base64 input");
                return null; // unreachable
            }
            // Reconstruct a latin-1 "binary string": one char
            // per byte, no encoding round-trip.
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes) sb.Append((char)b);
            return sb.ToString();
        });
    }

    private static string StripAsciiWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
