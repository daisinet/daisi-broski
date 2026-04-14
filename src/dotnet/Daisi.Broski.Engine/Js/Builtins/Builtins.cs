namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Central dispatcher for installing every phase 3a built-in
/// into a fresh <see cref="JsEngine"/>. Slice 6a installs the
/// global functions, the <c>Array</c> constructor + prototype,
/// and the <c>String</c> constructor + prototype. Slice 6b will
/// add <c>Object</c>, <c>Math</c>, <c>JSON</c>, and the
/// callback-taking <c>Array</c> methods. Slice 6c adds
/// <c>Number</c>, <c>Boolean</c>, and <c>Function.prototype</c>
/// methods.
/// </summary>
internal static class Builtins
{
    public static void Install(JsEngine engine)
    {
        // Error constructors install first so the VM can tag
        // internal runtime errors (from RaiseError) with proper
        // prototype chains before any other code runs.
        BuiltinError.Install(engine);
        BuiltinObject.Install(engine);
        BuiltinFunction.Install(engine);
        BuiltinMath.Install(engine);
        BuiltinJson.Install(engine);
        BuiltinGlobal.Install(engine);
        BuiltinSymbol.Install(engine);
        BuiltinArray.Install(engine);
        BuiltinString.Install(engine);
        BuiltinNumberBoolean.Install(engine);
        BuiltinDate.Install(engine);
        BuiltinConsole.Install(engine);
        BuiltinTimers.Install(engine);
        BuiltinCollections.Install(engine);
        BuiltinPromise.Install(engine);
        BuiltinTypedArrays.Install(engine);
        BuiltinUrl.Install(engine);
        BuiltinTextCoding.Install(engine);
        BuiltinBase64.Install(engine);
        BuiltinCrypto.Install(engine);
        BuiltinDomEvents.Install(engine);
        BuiltinFetch.Install(engine);
        BuiltinBlob.Install(engine);
        BuiltinFormData.Install(engine);
        BuiltinFileReader.Install(engine);
        BuiltinWebSocket.Install(engine);
        BuiltinIndexedDb.Install(engine);
        BuiltinProxy.Install(engine);
        BuiltinReflect.Install(engine);
        BuiltinAbort.Install(engine);
        BuiltinBigInt.Install(engine);
        BuiltinRegExp.Install(engine);
        BuiltinBrowserHost.Install(engine);
        BuiltinModernPrototypes.Install(engine);
    }

    /// <summary>
    /// Attach a native function as a non-enumerable method on
    /// the given object. Installs via
    /// <see cref="JsObject.SetNonEnumerable"/> so
    /// <c>for..in</c> and related enumeration paths don't walk
    /// over built-in prototype methods, matching how every
    /// browser treats the standard library.
    /// </summary>
    public static void Method(
        JsObject target,
        string name,
        Func<object?, IReadOnlyList<object?>, object?> impl)
    {
        target.SetNonEnumerable(name, new JsFunction(name, impl));
    }
}
