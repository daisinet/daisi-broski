namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Internal sentinel exception used by native built-in
/// implementations to raise a catchable JavaScript exception.
/// Native functions run synchronously inside
/// <see cref="JsVM.InvokeFunction"/>, so they can't directly call
/// the VM's <c>DoThrow</c> (it would return to the wrong point in
/// the stack). Instead they throw this sentinel, and
/// <c>InvokeFunction</c> catches it and routes the carried JS
/// value through <c>DoThrow</c>. From that point on the throw
/// behaves exactly as if a <c>throw</c> opcode had fired.
///
/// Not public — host code never constructs this directly.
/// Built-ins use the <see cref="JsThrow"/> helpers.
/// </summary>
internal sealed class JsThrowSignal : Exception
{
    public object? JsValue { get; }

    public JsThrowSignal(object? jsValue) : base("__js_throw_signal__")
    {
        JsValue = jsValue;
    }
}

/// <summary>
/// Helpers for native built-in implementations to raise
/// catchable JS exceptions. Each helper constructs a plain
/// <see cref="JsObject"/> with the canonical <c>name</c> and
/// <c>message</c> properties and throws a
/// <see cref="JsThrowSignal"/> that the VM's
/// <c>InvokeFunction</c> catches. Slice 6 will install real
/// constructors (<c>TypeError</c>, <c>RangeError</c>, ...) and
/// these helpers will be backed by them; for now they produce
/// plain objects matching the shape that slice 5 established
/// for internal errors.
///
/// The return type is <see cref="object"/> so callers can use
/// <c>return JsThrow.TypeError(...)</c> as a one-liner escape in
/// switch-style native implementations, even though the throw
/// means the return is never reached.
/// </summary>
public static class JsThrow
{
    public static object? TypeError(string message) => Raise("TypeError", message);
    public static object? RangeError(string message) => Raise("RangeError", message);
    public static object? SyntaxError(string message) => Raise("SyntaxError", message);
    public static object? ReferenceError(string message) => Raise("ReferenceError", message);
    public static object? Error(string message) => Raise("Error", message);

    private static object? Raise(string name, string message)
    {
        var err = new JsObject();
        err.Set("name", name);
        err.Set("message", message);
        throw new JsThrowSignal(err);
    }
}
