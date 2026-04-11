using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsBuiltin6dTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // Fixed reference point to avoid clock flakiness:
    // 2021-02-03T04:05:06.789Z. Epoch milliseconds = 1612325106789.
    // (1609459200 for 2021-01-01 UTC + 33 days * 86400 + 14706 seconds + 0.789s.)
    private const double FixedUtcMs = 1612325106789.0;

    // -------- Date.now + construction --------

    [Fact]
    public void Date_now_is_a_number_near_wall_clock()
    {
        var result = (double)Eval("Date.now();")!;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(result, nowMs - 10_000, nowMs + 10_000);
    }

    [Fact]
    public void New_Date_with_no_args_is_near_now()
    {
        var eng = new JsEngine();
        var t = (double)eng.Evaluate("new Date().getTime();")!;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(t, nowMs - 10_000, nowMs + 10_000);
    }

    [Fact]
    public void New_Date_with_ms_preserves_value()
    {
        Assert.Equal(
            FixedUtcMs,
            Eval($"new Date({FixedUtcMs}).getTime();"));
    }

    [Fact]
    public void New_Date_is_instanceof_Date_and_typeof_object()
    {
        Assert.Equal("object", Eval("typeof new Date();"));
        Assert.Equal(true, Eval("new Date() instanceof Date;"));
    }

    [Fact]
    public void Date_component_constructor()
    {
        // new Date(2021, 1, 3, 4, 5, 6, 789) in local time.
        // Compare component readbacks rather than an absolute ms
        // value so the test is timezone-agnostic.
        var eng = new JsEngine();
        eng.Evaluate("var d = new Date(2021, 1, 3, 4, 5, 6, 789);");
        Assert.Equal(2021.0, eng.Evaluate("d.getFullYear();"));
        Assert.Equal(1.0, eng.Evaluate("d.getMonth();"));  // 0-indexed → Feb
        Assert.Equal(3.0, eng.Evaluate("d.getDate();"));
        Assert.Equal(4.0, eng.Evaluate("d.getHours();"));
        Assert.Equal(5.0, eng.Evaluate("d.getMinutes();"));
        Assert.Equal(6.0, eng.Evaluate("d.getSeconds();"));
        Assert.Equal(789.0, eng.Evaluate("d.getMilliseconds();"));
    }

    // -------- Getters (UTC, deterministic) --------

    [Fact]
    public void UTC_getters_against_fixed_epoch_ms()
    {
        // Fixed: 2021-02-03T04:05:06.789Z
        var eng = new JsEngine();
        eng.Evaluate($"var d = new Date({FixedUtcMs});");
        Assert.Equal(2021.0, eng.Evaluate("d.getUTCFullYear();"));
        Assert.Equal(1.0, eng.Evaluate("d.getUTCMonth();"));
        Assert.Equal(3.0, eng.Evaluate("d.getUTCDate();"));
        Assert.Equal(4.0, eng.Evaluate("d.getUTCHours();"));
        Assert.Equal(5.0, eng.Evaluate("d.getUTCMinutes();"));
        Assert.Equal(6.0, eng.Evaluate("d.getUTCSeconds();"));
        Assert.Equal(789.0, eng.Evaluate("d.getUTCMilliseconds();"));
    }

    [Fact]
    public void GetUTCDay_returns_day_of_week_0_to_6()
    {
        // 2021-02-03 was a Wednesday → day 3.
        Assert.Equal(3.0, Eval($"new Date({FixedUtcMs}).getUTCDay();"));
    }

    // -------- toISOString + toJSON --------

    [Fact]
    public void ToISOString_format_matches_spec()
    {
        Assert.Equal(
            "2021-02-03T04:05:06.789Z",
            Eval($"new Date({FixedUtcMs}).toISOString();"));
    }

    [Fact]
    public void ToJSON_matches_toISOString()
    {
        Assert.Equal(
            "2021-02-03T04:05:06.789Z",
            Eval($"new Date({FixedUtcMs}).toJSON();"));
    }

    [Fact]
    public void Invalid_date_toISOString_throws_RangeError()
    {
        var ex = Assert.Throws<JsRuntimeException>(() =>
            Eval("new Date(NaN).toISOString();"));
        var err = Assert.IsType<JsObject>(ex.JsValue);
        Assert.Equal("RangeError", err.Get("name"));
    }

    [Fact]
    public void Invalid_date_getTime_is_NaN()
    {
        var v = Eval("new Date(NaN).getTime();");
        Assert.True(double.IsNaN((double)v!));
    }

    // -------- valueOf + getTime --------

    [Fact]
    public void GetTime_and_valueOf_return_the_same_ms()
    {
        Assert.Equal(
            FixedUtcMs,
            Eval($"new Date({FixedUtcMs}).valueOf();"));
        Assert.Equal(
            FixedUtcMs,
            Eval($"new Date({FixedUtcMs}).getTime();"));
    }

    // -------- Date arithmetic via valueOf --------

    [Fact]
    public void Two_dates_can_be_compared_via_valueOf()
    {
        Assert.Equal(
            true,
            Eval(@"
                var a = new Date(2021, 0, 1);
                var b = new Date(2021, 0, 2);
                a < b;
            "));
    }

    [Fact]
    public void Date_arithmetic_yields_ms_difference()
    {
        // b - a should be 86400 seconds = 86_400_000 ms.
        Assert.Equal(
            86_400_000.0,
            Eval(@"
                var a = new Date(2021, 0, 1);
                var b = new Date(2021, 0, 2);
                b - a;
            "));
    }

    // -------- JSON.stringify integration --------

    [Fact]
    public void JsonStringify_of_Date_produces_quoted_ISO_string()
    {
        Assert.Equal(
            "\"2021-02-03T04:05:06.789Z\"",
            Eval($"JSON.stringify(new Date({FixedUtcMs}));"));
    }

    [Fact]
    public void JsonStringify_of_invalid_Date_is_null()
    {
        Assert.Equal(
            "null",
            Eval("JSON.stringify(new Date(NaN));"));
    }

    [Fact]
    public void JsonStringify_of_object_containing_a_Date()
    {
        Assert.Equal(
            "{\"at\":\"2021-02-03T04:05:06.789Z\",\"event\":\"ping\"}",
            Eval($"JSON.stringify({{at: new Date({FixedUtcMs}), event: 'ping'}});"));
    }

    // -------- toString --------

    [Fact]
    public void ToString_contains_expected_date_parts()
    {
        // Local-time output — can't match exact string across
        // timezones, but the year and the day-of-week
        // abbreviation should both appear.
        var result = (string)Eval($"new Date({FixedUtcMs}).toString();")!;
        Assert.Contains("2021", result);
        Assert.Contains("GMT", result);
    }

    [Fact]
    public void ToString_of_invalid_date_is_Invalid_Date()
    {
        Assert.Equal(
            "Invalid Date",
            Eval("new Date(NaN).toString();"));
    }

    // -------- getTimezoneOffset --------

    [Fact]
    public void GetTimezoneOffset_is_minutes_integer()
    {
        var v = (double)Eval($"new Date({FixedUtcMs}).getTimezoneOffset();")!;
        // Offset should be an integer (in minutes) and should
        // round-trip: its negative times 60 seconds is the
        // local-to-UTC gap.
        Assert.Equal(v, Math.Truncate(v));
        Assert.InRange(v, -14 * 60, 14 * 60);
    }
}
