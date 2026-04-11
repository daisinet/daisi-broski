using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>console</c> — a global object with <c>log</c>,
/// <c>warn</c>, <c>error</c>, <c>info</c>, and <c>debug</c>
/// methods. Each formats its arguments space-separated via
/// <see cref="JsValue.ToJsString"/> and appends the resulting
/// line (plus a newline) to
/// <see cref="JsEngine.ConsoleOutput"/>. Hosts can read that
/// <see cref="StringBuilder"/> directly to capture script
/// output for display, logging, or assertions.
///
/// Slice-7 simplifications:
///
/// - No distinction between levels — all five methods write
///   to the same sink. Slice 7+ can split them if needed.
/// - No structured formatting for objects / arrays beyond
///   <c>ToJsString</c>. <c>console.log({a: 1})</c> emits
///   <c>[object Object]</c>, not <c>{ a: 1 }</c>.
///   <c>console.dir</c> / <c>table</c> / <c>trace</c> are
///   deferred.
/// - No format-string handling (<c>%s</c>, <c>%d</c>, ...).
/// </summary>
internal static class BuiltinConsole
{
    public static void Install(JsEngine engine)
    {
        var console = new JsObject { Prototype = engine.ObjectPrototype };

        Builtins.Method(console, "log", (t, a) => Write(engine, a));
        Builtins.Method(console, "info", (t, a) => Write(engine, a));
        Builtins.Method(console, "warn", (t, a) => Write(engine, a));
        Builtins.Method(console, "error", (t, a) => Write(engine, a));
        Builtins.Method(console, "debug", (t, a) => Write(engine, a));

        engine.Globals["console"] = console;
    }

    private static object? Write(JsEngine engine, IReadOnlyList<object?> args)
    {
        var sb = engine.ConsoleOutput;
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(JsValue.ToJsString(args[i]));
        }
        sb.Append('\n');
        return JsValue.Undefined;
    }
}
