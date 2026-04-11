namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Date value. Subclasses <see cref="JsObject"/> so it can be
/// used anywhere a regular object is expected (property access,
/// <c>instanceof</c>, etc.) while also carrying a specialized
/// <see cref="Time"/> slot — the number of milliseconds since
/// the Unix epoch, or <see cref="double.NaN"/> for an invalid
/// date. This matches the spec's <c>[[DateValue]]</c> internal
/// slot on ECMA-262 §15.9.
/// </summary>
public sealed class JsDate : JsObject
{
    /// <summary>
    /// Milliseconds since 1970-01-01T00:00:00.000 UTC.
    /// <see cref="double.NaN"/> if this is an "Invalid Date"
    /// (produced by constructing with <c>NaN</c> or by
    /// arithmetic on out-of-range inputs).
    /// </summary>
    public double Time { get; set; }

    public JsDate(JsObject prototype, double time)
    {
        Prototype = prototype;
        Time = time;
    }

    public bool IsValid => !double.IsNaN(Time) && !double.IsInfinity(Time);
}
