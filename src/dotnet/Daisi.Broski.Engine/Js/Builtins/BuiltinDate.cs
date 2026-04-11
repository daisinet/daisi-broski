using System.Globalization;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Date</c> constructor + static <c>Date.now</c> + read-only
/// prototype methods. Dates are stored as a
/// <see cref="JsDate.Time"/> slot — the ECMA-262 <c>[[DateValue]]</c>
/// — and converted to / from <see cref="DateTimeOffset"/> per
/// method.
///
/// Slice 6d scope:
///
/// - <b>Constructor</b>: <c>new Date()</c> (now),
///   <c>new Date(ms)</c> (numeric constructor),
///   <c>new Date(y, m, d, h, m, s, ms)</c> (component
///   constructor — any of <c>d</c>, <c>h</c>, <c>m</c>, <c>s</c>,
///   <c>ms</c> optional).
/// - <b>Static</b>: <c>Date.now()</c>.
/// - <b>Prototype</b>: <c>getTime</c>, <c>valueOf</c>,
///   <c>getFullYear</c>, <c>getMonth</c>, <c>getDate</c>,
///   <c>getDay</c>, <c>getHours</c>, <c>getMinutes</c>,
///   <c>getSeconds</c>, <c>getMilliseconds</c>,
///   <c>getTimezoneOffset</c>, UTC variants of all getters
///   (<c>getUTCFullYear</c> etc.), <c>toISOString</c>,
///   <c>toJSON</c>, <c>toString</c>.
///
/// Deferred:
///
/// - <b>Setters</b> (<c>setFullYear</c>, <c>setTime</c>, ...).
///   Most real-world Date use only reads components; setters
///   are rare and the spec algorithm for <c>MakeDay</c> /
///   <c>MakeTime</c> is fiddly.
/// - <b><c>Date.parse</c></b> and the string constructor —
///   browser date-string parsing is a maze of historical
///   formats with wildly different behavior across engines.
/// - <b><c>Date.UTC</c></b> — trivial once component
///   normalization is in place; deferred with the setters.
/// - <b><c>toLocaleString</c></b> and friends — locale output
///   is implementation-dependent; phase 3a's deferrals section
///   documents it.
/// </summary>
internal static class BuiltinDate
{
    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        engine.DatePrototype = proto;

        // Read-only instance methods.
        Builtins.Method(proto, "getTime", GetTime);
        Builtins.Method(proto, "valueOf", GetTime);

        // Local-time getters.
        Builtins.Method(proto, "getFullYear", (t, a) => LocalYear(t));
        Builtins.Method(proto, "getMonth", (t, a) => LocalMonth(t));
        Builtins.Method(proto, "getDate", (t, a) => LocalDay(t));
        Builtins.Method(proto, "getDay", (t, a) => LocalDayOfWeek(t));
        Builtins.Method(proto, "getHours", (t, a) => LocalHour(t));
        Builtins.Method(proto, "getMinutes", (t, a) => LocalMinute(t));
        Builtins.Method(proto, "getSeconds", (t, a) => LocalSecond(t));
        Builtins.Method(proto, "getMilliseconds", (t, a) => LocalMillisecond(t));
        Builtins.Method(proto, "getTimezoneOffset", GetTimezoneOffset);

        // UTC getters.
        Builtins.Method(proto, "getUTCFullYear", (t, a) => UtcYear(t));
        Builtins.Method(proto, "getUTCMonth", (t, a) => UtcMonth(t));
        Builtins.Method(proto, "getUTCDate", (t, a) => UtcDay(t));
        Builtins.Method(proto, "getUTCDay", (t, a) => UtcDayOfWeek(t));
        Builtins.Method(proto, "getUTCHours", (t, a) => UtcHour(t));
        Builtins.Method(proto, "getUTCMinutes", (t, a) => UtcMinute(t));
        Builtins.Method(proto, "getUTCSeconds", (t, a) => UtcSecond(t));
        Builtins.Method(proto, "getUTCMilliseconds", (t, a) => UtcMillisecond(t));

        // Stringification.
        Builtins.Method(proto, "toISOString", ToIsoString);
        Builtins.Method(proto, "toJSON", ToJson);
        Builtins.Method(proto, "toString", ToStringMethod);

        // Date constructor.
        var ctor = new JsFunction("Date", (thisVal, args) => Construct(engine, args));
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        Builtins.Method(ctor, "now", (t, a) => NowMs());

        engine.Globals["Date"] = ctor;
    }

    // -------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------

    private static object? Construct(JsEngine engine, IReadOnlyList<object?> args)
    {
        double time;
        if (args.Count == 0)
        {
            time = NowMs();
        }
        else if (args.Count == 1)
        {
            time = JsValue.ToNumber(args[0]);
        }
        else
        {
            // Component form: year, month, day?, hour?, min?, sec?, ms?
            int year = (int)JsValue.ToInt32(args[0]);
            int month = (int)JsValue.ToInt32(args[1]);
            int day = args.Count > 2 ? (int)JsValue.ToInt32(args[2]) : 1;
            int hour = args.Count > 3 ? (int)JsValue.ToInt32(args[3]) : 0;
            int minute = args.Count > 4 ? (int)JsValue.ToInt32(args[4]) : 0;
            int second = args.Count > 5 ? (int)JsValue.ToInt32(args[5]) : 0;
            int ms = args.Count > 6 ? (int)JsValue.ToInt32(args[6]) : 0;

            // ES5 two-digit-year rule: 0-99 is treated as 1900+year.
            if (year >= 0 && year <= 99) year += 1900;

            try
            {
                var dto = new DateTimeOffset(
                    new DateTime(year, month + 1, day, hour, minute, second, ms, DateTimeKind.Local));
                time = dto.ToUnixTimeMilliseconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                time = double.NaN;
            }
        }
        return new JsDate(engine.DatePrototype, time);
    }

    private static double NowMs() =>
        (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // -------------------------------------------------------------------
    // Instance extraction
    // -------------------------------------------------------------------

    private static JsDate RequireDate(object? thisVal, string method)
    {
        if (thisVal is JsDate d) return d;
        JsThrow.TypeError($"Date.prototype.{method} called on non-Date");
        return null!;
    }

    private static object? GetTime(object? thisVal, IReadOnlyList<object?> args)
    {
        var d = RequireDate(thisVal, "getTime");
        return d.Time;
    }

    // Local / UTC conversion helpers.

    private static DateTimeOffset? Local(object? thisVal)
    {
        var d = RequireDate(thisVal, "get*");
        if (!d.IsValid) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)d.Time).ToLocalTime();
    }

    private static DateTimeOffset? Utc(object? thisVal)
    {
        var d = RequireDate(thisVal, "getUTC*");
        if (!d.IsValid) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)d.Time).ToUniversalTime();
    }

    private static object? LocalYear(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)dto.Year : double.NaN;

    private static object? LocalMonth(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)(dto.Month - 1) : double.NaN;

    private static object? LocalDay(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)dto.Day : double.NaN;

    private static object? LocalDayOfWeek(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)(int)dto.DayOfWeek : double.NaN;

    private static object? LocalHour(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)dto.Hour : double.NaN;

    private static object? LocalMinute(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)dto.Minute : double.NaN;

    private static object? LocalSecond(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)dto.Second : double.NaN;

    private static object? LocalMillisecond(object? t) =>
        Local(t) is DateTimeOffset dto ? (double)dto.Millisecond : double.NaN;

    private static object? UtcYear(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)dto.Year : double.NaN;

    private static object? UtcMonth(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)(dto.Month - 1) : double.NaN;

    private static object? UtcDay(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)dto.Day : double.NaN;

    private static object? UtcDayOfWeek(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)(int)dto.DayOfWeek : double.NaN;

    private static object? UtcHour(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)dto.Hour : double.NaN;

    private static object? UtcMinute(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)dto.Minute : double.NaN;

    private static object? UtcSecond(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)dto.Second : double.NaN;

    private static object? UtcMillisecond(object? t) =>
        Utc(t) is DateTimeOffset dto ? (double)dto.Millisecond : double.NaN;

    /// <summary>
    /// ECMA §15.9.5.26 — <c>getTimezoneOffset()</c> returns the
    /// difference (in minutes) between local time and UTC:
    /// positive if local is behind UTC, negative if ahead.
    /// </summary>
    private static object? GetTimezoneOffset(object? thisVal, IReadOnlyList<object?> args)
    {
        var d = RequireDate(thisVal, "getTimezoneOffset");
        if (!d.IsValid) return double.NaN;
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)d.Time).ToLocalTime();
        return -(double)dto.Offset.TotalMinutes;
    }

    // -------------------------------------------------------------------
    // Stringification
    // -------------------------------------------------------------------

    /// <summary>
    /// ECMA §15.9.5.43 — <c>toISOString()</c>. Format:
    /// <c>YYYY-MM-DDTHH:mm:ss.sssZ</c> (UTC). Throws
    /// <c>RangeError</c> on an Invalid Date.
    /// </summary>
    public static string FormatIso(double time)
    {
        if (double.IsNaN(time) || double.IsInfinity(time))
        {
            JsThrow.RangeError("Invalid time value");
        }
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)time).ToUniversalTime();
        return dto.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static object? ToIsoString(object? thisVal, IReadOnlyList<object?> args)
    {
        var d = RequireDate(thisVal, "toISOString");
        return FormatIso(d.Time);
    }

    private static object? ToJson(object? thisVal, IReadOnlyList<object?> args)
    {
        // ES5 §15.9.5.44: toJSON should throw if toISOString
        // throws (e.g., Invalid Date). We replicate the
        // behavior exactly because our ToIsoString also throws.
        var d = RequireDate(thisVal, "toJSON");
        return FormatIso(d.Time);
    }

    /// <summary>
    /// ECMA §15.9.5.2 — <c>toString()</c>. Format is
    /// implementation-dependent. We emit a human-friendly
    /// representation close to what browsers do:
    /// <c>Tue Apr 11 2026 17:58:54 GMT-0500</c>.
    /// </summary>
    private static object? ToStringMethod(object? thisVal, IReadOnlyList<object?> args)
    {
        var d = RequireDate(thisVal, "toString");
        if (!d.IsValid) return "Invalid Date";
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)d.Time).ToLocalTime();
        var day = dto.DayOfWeek.ToString().Substring(0, 3);
        var month = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames[dto.Month - 1];
        var offsetHours = dto.Offset.Hours;
        var offsetMinutes = Math.Abs(dto.Offset.Minutes);
        var offsetSign = dto.Offset.Ticks >= 0 ? "+" : "-";
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2:00} {3:0000} {4:00}:{5:00}:{6:00} GMT{7}{8:00}{9:00}",
            day, month, dto.Day, dto.Year,
            dto.Hour, dto.Minute, dto.Second,
            offsetSign, Math.Abs(offsetHours), offsetMinutes);
    }
}
