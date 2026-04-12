namespace Daisi.Broski.Engine.Js.Dom;

/// <summary>
/// JS-side <c>Event</c> object. Subclass of
/// <see cref="JsObject"/> so the VM sees it as a normal value
/// for member access, <c>instanceof</c>, and property
/// enumeration. Instance state is exposed through
/// <see cref="Get"/> / <see cref="Set"/> overrides so the
/// propagation state written by
/// <see cref="JsDomNode.DispatchEvent"/> is reflected
/// immediately to listeners that read <c>event.eventPhase</c>
/// or <c>event.currentTarget</c>.
/// </summary>
public class JsDomEvent : JsObject
{
    // Spec-defined phase constants:
    //   NONE = 0, CAPTURING_PHASE = 1, AT_TARGET = 2, BUBBLING_PHASE = 3
    public const int PhaseNone = 0;
    public const int PhaseCapturing = 1;
    public const int PhaseAtTarget = 2;
    public const int PhaseBubbling = 3;

    public string Type { get; }
    public bool Bubbles { get; }
    public bool Cancelable { get; }
    public double TimeStamp { get; }

    // Target and CurrentTarget are typed as JsObject? so
    // non-DOM event sources (e.g. JsAbortSignal) can fill
    // them in. JsDomNode values still flow through verbatim
    // because JsDomNode is-a JsObject.
    public JsObject? Target { get; internal set; }
    public JsObject? CurrentTarget { get; internal set; }
    public int EventPhase { get; internal set; } = PhaseNone;
    public bool DefaultPrevented { get; internal set; }
    public bool PropagationStopped { get; internal set; }
    public bool ImmediatePropagationStopped { get; internal set; }

    public JsDomEvent(string type, bool bubbles, bool cancelable)
    {
        Type = type;
        Bubbles = bubbles;
        Cancelable = cancelable;
        TimeStamp = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        InstallMethods();
    }

    private void InstallMethods()
    {
        SetNonEnumerable("preventDefault", new JsFunction("preventDefault", (thisVal, args) =>
        {
            if (Cancelable) DefaultPrevented = true;
            return JsValue.Undefined;
        }));
        SetNonEnumerable("stopPropagation", new JsFunction("stopPropagation", (thisVal, args) =>
        {
            PropagationStopped = true;
            return JsValue.Undefined;
        }));
        SetNonEnumerable("stopImmediatePropagation", new JsFunction("stopImmediatePropagation", (thisVal, args) =>
        {
            PropagationStopped = true;
            ImmediatePropagationStopped = true;
            return JsValue.Undefined;
        }));
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        switch (key)
        {
            case "type": return Type;
            case "bubbles": return Bubbles;
            case "cancelable": return Cancelable;
            case "defaultPrevented": return DefaultPrevented;
            case "eventPhase": return (double)EventPhase;
            case "target": return (object?)Target ?? JsValue.Null;
            case "currentTarget": return (object?)CurrentTarget ?? JsValue.Null;
            case "timeStamp": return TimeStamp;
            case "NONE": return (double)PhaseNone;
            case "CAPTURING_PHASE": return (double)PhaseCapturing;
            case "AT_TARGET": return (double)PhaseAtTarget;
            case "BUBBLING_PHASE": return (double)PhaseBubbling;
        }
        return base.Get(key);
    }

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        // The core event fields are read-only from script.
        // Silently ignore writes to match browser non-strict
        // behavior.
        switch (key)
        {
            case "type":
            case "bubbles":
            case "cancelable":
            case "defaultPrevented":
            case "eventPhase":
            case "target":
            case "currentTarget":
            case "timeStamp":
                return;
        }
        base.Set(key, value);
    }
}

/// <summary>
/// <c>CustomEvent</c>: adds a script-controlled
/// <c>detail</c> payload on top of <see cref="JsDomEvent"/>.
/// </summary>
public sealed class JsDomCustomEvent : JsDomEvent
{
    public object? Detail { get; }

    public JsDomCustomEvent(string type, bool bubbles, bool cancelable, object? detail)
        : base(type, bubbles, cancelable)
    {
        Detail = detail;
    }

    /// <inheritdoc />
    public override object? Get(string key)
    {
        if (key == "detail") return Detail ?? JsValue.Null;
        return base.Get(key);
    }
}
