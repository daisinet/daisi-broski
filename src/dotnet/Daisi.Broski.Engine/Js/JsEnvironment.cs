namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Lexical environment record — a scope's local bindings plus a
/// reference to the enclosing scope for name resolution. The VM
/// threads a pointer to the "current" environment through every
/// execution; function calls push a new env whose parent is the
/// captured env of the called function, and returns pop it.
///
/// A closure is implicit: when a <see cref="JsFunction"/> is
/// materialized by <see cref="OpCode.MakeFunction"/>, it stores the
/// current env by reference. Any inner references to names defined
/// in enclosing scopes resolve via that env chain, and .NET keeps
/// the envs alive as long as any reachable function references
/// them — which is exactly the JavaScript closure semantics, riding
/// on top of the .NET GC per DD-05 option A.
///
/// For the top-level program, the "environment" is the globals
/// dictionary that the caller owns. <see cref="JsEngine"/> wraps
/// its <c>Globals</c> in an env whose bindings *are* the same
/// dictionary, so top-level code continues to read and write the
/// user-visible globals while inner functions get their own scopes.
/// </summary>
public sealed class JsEnvironment
{
    /// <summary>Own bindings of this environment.</summary>
    public Dictionary<string, object?> Bindings { get; }

    /// <summary>Enclosing scope, or <c>null</c> if this is the global env.</summary>
    public JsEnvironment? Parent { get; }

    public JsEnvironment(Dictionary<string, object?> bindings, JsEnvironment? parent)
    {
        Bindings = bindings;
        Parent = parent;
    }

    public JsEnvironment(JsEnvironment? parent) : this(new Dictionary<string, object?>(), parent) { }

    /// <summary>
    /// Walk the env chain looking for <paramref name="name"/>.
    /// Returns <c>true</c> with the found value if any ancestor
    /// has the binding; otherwise <c>false</c>.
    /// </summary>
    public bool TryResolve(string name, out object? value)
    {
        for (var env = this; env is not null; env = env.Parent)
        {
            if (env.Bindings.TryGetValue(name, out value)) return true;
        }
        value = null;
        return false;
    }

    /// <summary>
    /// Assign to an existing binding somewhere up the chain. If no
    /// ancestor has it, create the binding on the root env (sloppy
    /// mode auto-globals). Matches ECMA §10.2.2 GetIdentifierReference
    /// / PutValue semantics with strict mode disabled.
    /// </summary>
    public void Assign(string name, object? value)
    {
        for (var env = this; env is not null; env = env.Parent)
        {
            if (env.Bindings.ContainsKey(name))
            {
                env.Bindings[name] = value;
                return;
            }
        }
        // Not found — create on the root (globals) env.
        var root = this;
        while (root.Parent is not null) root = root.Parent;
        root.Bindings[name] = value;
    }

    /// <summary>
    /// Declare a <c>var</c>-style binding in this environment
    /// specifically, initialized to <c>undefined</c> if not already
    /// present. Used for hoisted variable declarations; the
    /// top-level equivalent creates globals, inside a function it
    /// creates function-local bindings.
    /// </summary>
    public void DeclareLocal(string name)
    {
        if (!Bindings.ContainsKey(name))
        {
            Bindings[name] = JsValue.Undefined;
        }
    }
}
