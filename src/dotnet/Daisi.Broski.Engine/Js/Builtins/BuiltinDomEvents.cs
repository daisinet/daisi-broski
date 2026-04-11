using Daisi.Broski.Engine.Js.Dom;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>Event</c> and <c>CustomEvent</c> constructors. These
/// live on the engine's globals regardless of whether a
/// <see cref="Daisi.Broski.Engine.Dom.Document"/> has been
/// attached — script code commonly synthesizes events in
/// isolation (tests, frameworks) without ever dispatching
/// them through the DOM tree, and we want the constructors
/// to be available in that case.
///
/// The installed prototypes are stored on the engine so
/// <see cref="JsDomNode.DispatchEvent"/> can tell whether
/// an argument is an instance of the right hierarchy —
/// currently handled by C# type check, but the prototype
/// plumbing is in place for <c>evt instanceof Event</c>
/// to work from script.
/// </summary>
internal static class BuiltinDomEvents
{
    public static void Install(JsEngine engine)
    {
        var eventProto = new JsObject { Prototype = engine.ObjectPrototype };
        engine.EventPrototype = eventProto;

        var eventCtor = new JsFunction("Event", (thisVal, args) =>
        {
            if (args.Count == 0)
            {
                JsThrow.TypeError("Event constructor requires a type argument");
            }
            var type = JsValue.ToJsString(args[0]);
            (bool bubbles, bool cancelable) = ReadEventInit(args.Count > 1 ? args[1] : JsValue.Undefined);
            return new JsDomEvent(type, bubbles, cancelable) { Prototype = eventProto };
        });
        eventCtor.SetNonEnumerable("prototype", eventProto);
        eventProto.SetNonEnumerable("constructor", eventCtor);
        // Expose the phase constants as statics on the
        // constructor too so `Event.AT_TARGET` works.
        eventCtor.SetNonEnumerable("NONE", (double)JsDomEvent.PhaseNone);
        eventCtor.SetNonEnumerable("CAPTURING_PHASE", (double)JsDomEvent.PhaseCapturing);
        eventCtor.SetNonEnumerable("AT_TARGET", (double)JsDomEvent.PhaseAtTarget);
        eventCtor.SetNonEnumerable("BUBBLING_PHASE", (double)JsDomEvent.PhaseBubbling);
        engine.Globals["Event"] = eventCtor;

        var customProto = new JsObject { Prototype = eventProto };
        engine.CustomEventPrototype = customProto;

        var customCtor = new JsFunction("CustomEvent", (thisVal, args) =>
        {
            if (args.Count == 0)
            {
                JsThrow.TypeError("CustomEvent constructor requires a type argument");
            }
            var type = JsValue.ToJsString(args[0]);
            (bool bubbles, bool cancelable) = ReadEventInit(args.Count > 1 ? args[1] : JsValue.Undefined);
            object? detail = null;
            if (args.Count > 1 && args[1] is JsObject init && init.Get("detail") is var d && d is not JsUndefined)
            {
                detail = d;
            }
            return new JsDomCustomEvent(type, bubbles, cancelable, detail) { Prototype = customProto };
        });
        customCtor.SetNonEnumerable("prototype", customProto);
        customProto.SetNonEnumerable("constructor", customCtor);
        engine.Globals["CustomEvent"] = customCtor;
    }

    /// <summary>
    /// Extract <c>bubbles</c> and <c>cancelable</c> from the
    /// <c>EventInit</c> dictionary argument. Missing keys
    /// default to <c>false</c>, matching the spec.
    /// </summary>
    private static (bool bubbles, bool cancelable) ReadEventInit(object? init)
    {
        if (init is not JsObject obj) return (false, false);
        bool bubbles = JsValue.ToBoolean(obj.Get("bubbles"));
        bool cancelable = JsValue.ToBoolean(obj.Get("cancelable"));
        return (bubbles, cancelable);
    }
}
