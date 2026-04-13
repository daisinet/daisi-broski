using System.Text;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js.Dom;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Top-level facade for the phase-3a JavaScript engine. Parses
/// source text into an AST, compiles it into bytecode, and runs it
/// on the stack VM, returning the completion value of the last
/// evaluated expression (ECMA §14 top-level completion semantics).
///
/// Each <see cref="JsEngine"/> instance owns its own globals
/// dictionary plus the well-known prototype objects
/// (<see cref="ArrayPrototype"/>, <see cref="StringPrototype"/>,
/// etc.) that the VM consults when it encounters property access
/// on a primitive. Built-ins (<c>parseInt</c>,
/// <c>Array.prototype.push</c>, and friends) are installed in
/// the constructor, so successive <see cref="Evaluate"/> calls
/// on the same engine see a stable, populated global environment.
///
/// Not thread-safe: do not call methods on the same engine
/// instance from multiple threads.
/// </summary>
public sealed class JsEngine
{
    /// <summary>
    /// Global variable bindings. Exposed directly so tests and
    /// host code can set values before <see cref="Evaluate"/> runs
    /// and inspect them afterward. Pre-seeded with the ES5
    /// read-only globals (<c>undefined</c>, <c>NaN</c>,
    /// <c>Infinity</c>) and the built-in constructors /
    /// functions installed by slice 6.
    /// </summary>
    public Dictionary<string, object?> Globals { get; } = new();

    /// <summary>
    /// Root of the prototype chain for every object the VM
    /// allocates via a literal. Slice 6a doesn't yet install
    /// <c>Object.prototype</c> methods (<c>hasOwnProperty</c>,
    /// <c>toString</c>, etc.) — slice 6b adds those.
    /// </summary>
    public JsObject ObjectPrototype { get; }

    /// <summary>
    /// Prototype of every <see cref="JsArray"/>. The VM sets
    /// <c>CreateArray</c>'s output's prototype to this, and
    /// <c>Array.prototype.*</c> methods live here.
    /// </summary>
    public JsObject ArrayPrototype { get; }

    /// <summary>
    /// Prototype that the VM consults when a property is read
    /// from a string primitive. <c>String.prototype.charAt</c>
    /// and friends live here.
    /// </summary>
    public JsObject StringPrototype { get; }

    /// <summary>Number primitive prototype. Slice 6c populates.</summary>
    public JsObject NumberPrototype { get; }

    /// <summary>Boolean primitive prototype. Slice 6c populates.</summary>
    public JsObject BooleanPrototype { get; }

    /// <summary>
    /// <c>Function.prototype</c>. Every <see cref="JsFunction"/>
    /// value has this as its <c>[[Prototype]]</c> — that's what
    /// makes <c>fn.call(...)</c> / <c>fn.apply(...)</c> /
    /// <c>fn.bind(...)</c> resolve via the normal property
    /// lookup path. Distinct from
    /// <see cref="JsFunction.FunctionPrototype"/>, which is the
    /// per-function <c>.prototype</c> property used by
    /// <c>new F(...)</c> to set an instance's prototype.
    /// </summary>
    public JsObject FunctionPrototype { get; }

    /// <summary>
    /// <c>Date.prototype</c>. Installed by slice 6d; carries
    /// <c>getTime</c> / <c>valueOf</c> / <c>getFullYear</c> /
    /// <c>toISOString</c> / etc. Every <see cref="JsDate"/>
    /// value has this as its <c>[[Prototype]]</c>.
    /// </summary>
    public JsObject DatePrototype { get; internal set; } = null!;

    /// <summary>
    /// Root <c>Error.prototype</c>. Installed by slice 6b; used
    /// by <see cref="JsVM"/> when <c>RaiseError</c> creates an
    /// error object with no more specific subtype.
    /// </summary>
    public JsObject ErrorPrototype { get; internal set; } = null!;

    /// <summary><c>TypeError.prototype</c>.</summary>
    public JsObject TypeErrorPrototype { get; internal set; } = null!;

    /// <summary><c>RangeError.prototype</c>.</summary>
    public JsObject RangeErrorPrototype { get; internal set; } = null!;

    /// <summary><c>SyntaxError.prototype</c>.</summary>
    public JsObject SyntaxErrorPrototype { get; internal set; } = null!;

    /// <summary><c>ReferenceError.prototype</c>.</summary>
    public JsObject ReferenceErrorPrototype { get; internal set; } = null!;

    /// <summary>
    /// Well-known <c>Symbol.iterator</c> — the unique key used
    /// to look up the iterator factory method on an iterable.
    /// Every built-in iterable (Array, String) installs its
    /// iterator factory under this key on its prototype; user
    /// code can install its own by storing a function under
    /// <c>obj[Symbol.iterator] = function() { ... }</c>.
    /// </summary>
    public JsSymbol IteratorSymbol { get; } = new JsSymbol("Symbol.iterator");

    /// <summary><c>Promise.prototype</c> — installed by
    /// <c>BuiltinPromise</c>. Exposed as a property so the
    /// <see cref="JsPromise"/> constructor can set its new
    /// instances' prototype at allocation time.</summary>
    public JsObject PromisePrototype { get; internal set; } = null!;

    /// <summary>
    /// <c>URLSearchParams.prototype</c> — installed by
    /// <c>BuiltinUrl</c>. Exposed so <see cref="JsUrl"/>
    /// can lazily construct its <c>searchParams</c> view
    /// with the right prototype when script reads
    /// <c>url.searchParams</c>.
    /// </summary>
    public JsObject UrlSearchParamsPrototype { get; internal set; } = null!;

    /// <summary>
    /// <c>Event.prototype</c> — installed by
    /// <c>BuiltinDomEvents</c>. Used as the chain root for
    /// every <c>Event</c> / <c>CustomEvent</c> instance so
    /// <c>evt instanceof Event</c> walks the right prototype
    /// chain.
    /// </summary>
    public JsObject EventPrototype { get; internal set; } = null!;

    /// <summary>
    /// <c>CustomEvent.prototype</c>. Chains to
    /// <see cref="EventPrototype"/> so a <c>CustomEvent</c>
    /// is also an <c>Event</c> via <c>instanceof</c>.
    /// </summary>
    public JsObject CustomEventPrototype { get; internal set; } = null!;

    /// <summary>
    /// <c>RegExp.prototype</c> — installed by
    /// <c>BuiltinRegExp</c>. Exposed so the VM's
    /// <see cref="OpCode.NewRegExp"/> handler can set
    /// fresh <see cref="JsRegExp"/> instances' prototype
    /// without threading a separate reference through
    /// bytecode operands.
    /// </summary>
    public JsObject RegExpPrototype { get; internal set; } = null!;

    /// <summary>
    /// Module cache keyed by resolved URL. Populated and
    /// read by <see cref="ImportModule"/>. A module is
    /// inserted into the cache as soon as it is created
    /// (before evaluation starts) so subsequent imports of
    /// the same URL share the same exports object.
    /// </summary>
    private readonly Dictionary<string, JsModule> _moduleCache = new();

    /// <summary>
    /// Host-provided hook that turns a specifier +
    /// referrer URL into a resolved URL + source text.
    /// Set this before calling <see cref="ImportModule"/>.
    /// </summary>
    public ModuleResolver? ModuleResolver { get; set; }

    /// <summary>
    /// Pluggable handler for the <c>fetch</c> global. The
    /// default handler (installed at construction) wraps
    /// the engine's <see cref="Net.HttpFetcher"/> so real
    /// HTTPS requests run through the same phase-1 network
    /// stack the CLI and sandbox already use. Tests install
    /// a stub handler that returns canned responses without
    /// touching the network, keeping the engine test suite
    /// offline.
    /// </summary>
    public FetchHandler? FetchHandler { get; set; }

    /// <summary>
    /// Persistent VM owned by this engine. Reused across every
    /// <see cref="Evaluate"/> call and every event-loop task
    /// so scheduled callbacks see bindings established by the
    /// main script.
    /// </summary>
    public JsVM Vm { get; }

    /// <summary>
    /// Host-side event loop that drives <c>setTimeout</c>,
    /// <c>setInterval</c>, <c>queueMicrotask</c>, and any
    /// other asynchronous work scheduled from script. Exposed
    /// so hosts can drain it (via <see cref="DrainEventLoop"/>)
    /// at the point that makes sense for their application.
    /// </summary>
    public JsEventLoop EventLoop { get; }

    /// <summary>
    /// Collected <c>console.log</c> / <c>warn</c> / <c>error</c>
    /// output. Tests inspect <see cref="StringBuilder.ToString"/>
    /// on this to verify script-level logging.
    /// </summary>
    public StringBuilder ConsoleOutput { get; } = new();

    /// <summary>
    /// DOM wrapper factory. Null until
    /// <see cref="AttachDocument"/> is called — the JS engine
    /// runs perfectly well without a DOM for headless scripting,
    /// test262, and standalone tool use. When a document is
    /// attached, this is the bridge that issues one
    /// <see cref="JsDomNode"/> per backing <see cref="Node"/>.
    /// </summary>
    public JsDomBridge? DomBridge { get; private set; }

    /// <summary>
    /// The currently attached document, exposed so host code
    /// that already holds the C# <see cref="Document"/> doesn't
    /// need to thread it separately. Matches the
    /// <c>document</c> global visible to script.
    /// </summary>
    public Document? AttachedDocument { get; private set; }

    public JsEngine()
    {
        // Seed the read-only globals before installing built-ins
        // so Builtins.Install can reference them if needed.
        Globals["undefined"] = JsValue.Undefined;
        Globals["NaN"] = double.NaN;
        Globals["Infinity"] = double.PositiveInfinity;

        // Create prototype objects in dependency order.
        ObjectPrototype = new JsObject();
        ArrayPrototype = new JsObject { Prototype = ObjectPrototype };
        StringPrototype = new JsObject { Prototype = ObjectPrototype };
        NumberPrototype = new JsObject { Prototype = ObjectPrototype };
        BooleanPrototype = new JsObject { Prototype = ObjectPrototype };
        FunctionPrototype = new JsObject { Prototype = ObjectPrototype };

        // Create the persistent VM and event loop. These are
        // constructed before built-ins install because some
        // built-ins (setTimeout, console) reference them.
        Vm = new JsVM(this);
        EventLoop = new JsEventLoop(this);

        Builtins.Install(this);

        // Default fetch handler: delegates to a shared
        // HttpFetcher instance owned by the engine. Tests
        // overwrite this with a stub before invoking any
        // fetch from script so the suite stays offline.
        // The HttpFetcher is lazily constructed on first
        // use to avoid creating an HttpClient when script
        // never touches fetch.
        FetchHandler = DefaultFetchHandler;

        // Hidden global used by the compiler to implement
        // dynamic `import('./mod')`. Not enumerable under
        // any user-visible name, just under a reserved
        // prefix the parser rewrites `import(...)` to.
        Globals["$importModule"] = new JsFunction("$importModule", (thisVal, args) =>
        {
            var specifier = args.Count > 0 ? JsValue.ToJsString(args[0]) : "";
            var promise = new JsPromise(this);
            try
            {
                var mod = ImportModule(specifier);
                promise.Resolve(mod.Exports);
            }
            catch (JsRuntimeException rex)
            {
                promise.Reject(rex.JsValue);
            }
            catch (Exception ex)
            {
                // Host-side errors (missing resolver etc.)
                // surface as rejected promises.
                var err = new JsObject { Prototype = ErrorPrototype };
                err.Set("name", "Error");
                err.Set("message", ex.Message);
                promise.Reject(err);
            }
            return promise;
        });

        // Post-install: set [[Prototype]] = FunctionPrototype on
        // every JsFunction reachable from the engine so
        // `fn.call(...)` / `fn.apply(...)` / `fn.bind(...)`
        // resolve via the standard prototype-chain lookup.
        // User functions created later via the MakeFunction
        // opcode get their Prototype set at creation time by
        // the VM.
        FixupFunctionPrototypes();
    }

    /// <summary>
    /// Attach a <see cref="Document"/> to this engine, installing
    /// the script-visible <c>document</c> global (and <c>window</c>
    /// as a self-reference to <see cref="Globals"/>). After this
    /// call, script code can reach every DOM node through
    /// <c>document.querySelector</c> / <c>getElementById</c> /
    /// etc., and any node returned into JS flows through the
    /// wrapper cache so <c>===</c> identity is stable.
    ///
    /// Calling <c>AttachDocument</c> a second time replaces the
    /// previous binding — the old wrappers are not invalidated
    /// (scripts still holding a reference can keep using them)
    /// but new lookups go through a fresh bridge against the
    /// new document. This matches the way a host process
    /// typically reuses one engine across page loads.
    /// </summary>
    public void AttachDocument(Document document, Uri? pageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        AttachedDocument = document;
        DomBridge = new JsDomBridge(this);
        Globals["document"] = DomBridge.Wrap(document);

        // Remember the URL the document was loaded from so
        // `location.href` + relative-URL-resolution against
        // `new URL('.', location.href)` produce sensible
        // values. When no URL is given we fall back to
        // about:blank which is what scripts see before any
        // navigation in a real browser.
        AttachedPageUrl = pageUrl;
        if (Globals.TryGetValue("location", out var locVal) && locVal is JsLocation loc)
        {
            loc.OnPageLoaded(pageUrl);
        }

        // `window` is the global object in a browser. We
        // expose a minimal shim: reads and writes go through
        // the engine's Globals dictionary, so
        // `window.location` etc. can be populated by host
        // code via `Globals["location"] = ...`. Future slices
        // can layer a richer wrapper on top without breaking
        // existing users.
        if (!Globals.ContainsKey("window"))
        {
            Globals["window"] = new JsWindowProxy(this);
        }
    }

    /// <summary>
    /// URL the currently attached document was loaded from,
    /// or <c>null</c> when none was provided. Used by
    /// <see cref="JsLocation"/> to back <c>location.href</c>
    /// with a real URL so scripts that resolve
    /// <c>new URL('.', location.href)</c> don't crash on
    /// the about:blank placeholder.
    /// </summary>
    public Uri? AttachedPageUrl { get; private set; }

    /// <summary>
    /// The DOM <c>&lt;script&gt;</c> element whose source
    /// the engine is currently evaluating, set by the host
    /// driver around each <see cref="RunScript"/> call.
    /// Surfaces as <c>document.currentScript</c> so
    /// bootstrap scripts that mount next to their own
    /// <c>&lt;script&gt;</c> tag (SvelteKit, Next.js, and
    /// other SSR frameworks all do this) can find their
    /// parent element.
    /// </summary>
    public Daisi.Broski.Engine.Dom.Element? ExecutingScript { get; set; }

    /// <summary>
    /// Shared <see cref="Net.HttpFetcher"/> for the default
    /// fetch handler. Lazily constructed on first request
    /// so engines that never call fetch don't pay the
    /// <see cref="HttpClient"/> startup cost.
    /// </summary>
    private Net.HttpFetcher? _sharedHttpFetcher;

    /// <summary>
    /// Default implementation of <see cref="FetchHandler"/>.
    /// Runs the request synchronously on the VM thread via
    /// <c>.GetAwaiter().GetResult()</c> — the VM is
    /// single-threaded anyway, so blocking here is
    /// equivalent to the script awaiting a real promise.
    /// Returns a <see cref="JsFetchResponse"/> populated
    /// from the <see cref="Net.FetchResult"/>.
    /// </summary>
    private JsFetchResponse DefaultFetchHandler(JsFetchRequest request)
    {
        _sharedHttpFetcher ??= new Net.HttpFetcher();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid URL: '{request.Url}'");
        }
        var result = _sharedHttpFetcher.FetchAsync(uri).GetAwaiter().GetResult();
        var headers = new JsHeaders();
        if (Globals.TryGetValue("Headers", out var hctor) &&
            hctor is JsFunction hfn &&
            hfn.Get("prototype") is JsObject hproto)
        {
            headers.Prototype = hproto;
        }
        foreach (var kv in result.Headers)
        {
            foreach (var value in kv.Value)
            {
                headers.Append(kv.Key, value);
            }
        }
        return new JsFetchResponse
        {
            Status = (int)result.Status,
            StatusText = result.Status.ToString(),
            Url = result.FinalUrl.ToString(),
            Headers = headers,
            Body = result.Body,
        };
    }

    /// <summary>
    /// Walk all objects reachable from <see cref="Globals"/> and
    /// the well-known prototypes, and set
    /// <c>JsFunction.Prototype</c> to <see cref="FunctionPrototype"/>
    /// for any function that doesn't already have one. Called
    /// once at the end of the engine constructor.
    /// </summary>
    private void FixupFunctionPrototypes()
    {
        var visited = new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        var roots = new JsObject[]
        {
            ObjectPrototype, ArrayPrototype, StringPrototype,
            NumberPrototype, BooleanPrototype, FunctionPrototype,
            ErrorPrototype, TypeErrorPrototype, RangeErrorPrototype,
            SyntaxErrorPrototype, ReferenceErrorPrototype,
            DatePrototype, RegExpPrototype,
        };
        foreach (var root in roots)
        {
            if (root is not null) WalkForFunctions(root, visited);
        }
        foreach (var kv in Globals)
        {
            WalkValueForFunctions(kv.Value, visited);
        }
    }

    private void WalkValueForFunctions(object? value, HashSet<JsObject> visited)
    {
        if (value is JsObject obj) WalkForFunctions(obj, visited);
    }

    private void WalkForFunctions(JsObject obj, HashSet<JsObject> visited)
    {
        if (!visited.Add(obj)) return;
        if (obj is JsFunction fn && fn.Prototype is null)
        {
            fn.Prototype = FunctionPrototype;
        }
        foreach (var kv in obj.Properties)
        {
            WalkValueForFunctions(kv.Value, visited);
        }
        // Accessor-descriptor getters / setters also need the
        // Function.prototype link so scripts can do
        // `Object.getOwnPropertyDescriptor(x, 'y').get.call(...)`.
        foreach (var kv in obj.OwnAccessors())
        {
            if (kv.Value.Getter is JsFunction g && g.Prototype is null)
                g.Prototype = FunctionPrototype;
            if (kv.Value.Setter is JsFunction s && s.Prototype is null)
                s.Prototype = FunctionPrototype;
        }
    }

    /// <summary>
    /// Parse, compile, and run <paramref name="source"/> on this
    /// engine's persistent <see cref="Vm"/>. Returns the value
    /// of the last top-level expression statement. Does <i>not</i>
    /// drain the event loop — call <see cref="DrainEventLoop"/>
    /// (or use <see cref="RunScript"/>) when you want scheduled
    /// callbacks to fire.
    /// </summary>
    public object? Evaluate(string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = new JsCompiler().Compile(program);
        return Vm.RunChunk(chunk);
    }

    /// <summary>
    /// Parse, compile, run <paramref name="source"/>, and then
    /// drain the event loop until it is idle. Use this when the
    /// caller expects <c>setTimeout</c> / <c>setInterval</c> /
    /// <c>queueMicrotask</c> callbacks to complete before
    /// control returns.
    /// </summary>
    public object? RunScript(string source)
    {
        var completion = Evaluate(source);
        DrainEventLoop();
        return completion;
    }

    /// <summary>
    /// Run queued tasks, microtasks, and timers until the event
    /// loop is idle. Used by <see cref="RunScript"/> and by
    /// tests that want to step the event loop manually after an
    /// <see cref="Evaluate"/>. An uncaught throw from any task
    /// escapes as a <see cref="JsRuntimeException"/> whose
    /// <see cref="JsRuntimeException.JsValue"/> carries the
    /// thrown value.
    /// </summary>
    public void DrainEventLoop()
    {
        try
        {
            EventLoop.Drain();
        }
        catch (JsThrowSignal sig)
        {
            // The VM's internal throw-propagation signal
            // escaped all the way out of every native boundary
            // without finding a handler. Surface it to the
            // host as a normal uncaught JS exception.
            throw new JsRuntimeException(
                $"Uncaught {JsValue.ToJsString(sig.JsValue)}",
                sig.JsValue);
        }
    }

    /// <summary>
    /// Top-level entry point for loading an ES2015 module.
    /// Resolves <paramref name="specifier"/> via the
    /// configured <see cref="ModuleResolver"/>, parses it,
    /// loads its imports recursively, and evaluates it. The
    /// returned <see cref="JsModule.Exports"/> object is the
    /// module's exports namespace — its own properties are
    /// the named exports plus optional <c>default</c>.
    ///
    /// Modules are cached by resolved URL, so a subsequent
    /// import of the same specifier returns the same
    /// <see cref="JsModule"/> without re-running the body.
    /// </summary>
    public JsModule ImportModule(string specifier, string? referrerUrl = null)
    {
        if (ModuleResolver is null)
        {
            throw new InvalidOperationException(
                "JsEngine.ModuleResolver must be set before importing modules");
        }
        var resolved = ModuleResolver(specifier, referrerUrl);
        if (resolved is null)
        {
            throw new JsRuntimeException(
                $"Cannot find module '{specifier}'",
                MakeTypeError($"Cannot find module '{specifier}'"));
        }
        var r = resolved.Value;
        if (_moduleCache.TryGetValue(r.Url, out var cached))
        {
            // Even if the cached module is still loading
            // (i.e., we're in the middle of a cycle),
            // return it so imports resolve. Bindings that
            // haven't been set yet will be undefined — the
            // caller is responsible for not accessing them
            // until after evaluation completes.
            return cached;
        }

        var module = new JsModule(
            url: r.Url,
            exports: new JsObject { Prototype = ObjectPrototype });
        _moduleCache[r.Url] = module;

        // Parse.
        var program = new JsParser(r.Source).ParseProgram();

        // Recursively resolve imports — each one becomes
        // an evaluated dependency module whose exports we
        // copy into the current module's initial bindings.
        var importBindings = new Dictionary<string, object?>();
        foreach (var stmt in program.Body)
        {
            if (stmt is ImportDeclaration imp)
            {
                var dep = ImportModule(imp.Source, r.Url);
                foreach (var spec in imp.Specifiers)
                {
                    if (spec.IsNamespace)
                    {
                        importBindings[spec.Local] = dep.Exports;
                    }
                    else if (spec.IsDefault)
                    {
                        importBindings[spec.Local] = dep.Exports.Get("default");
                    }
                    else
                    {
                        importBindings[spec.Local] = dep.Exports.Get(spec.Imported);
                    }
                }
            }
        }

        // Compile in module mode: exports are redirected to
        // module.Exports, and imported bindings are installed
        // in a fresh module-local env chained above globals.
        module.State = ModuleState.Evaluating;
        var compiler = new JsCompiler();
        var chunk = compiler.CompileModule(program, importBindings);

        // Install the module's locals-env on the VM, with
        // `$exports` pre-bound so export-rewriting can reach
        // it by name. We temporarily swap in a fresh env
        // chained above globals; after evaluation the old
        // env is restored so unrelated scripts on the same
        // engine aren't disturbed.
        Vm.RunModuleChunk(chunk, importBindings, module.Exports);

        // Re-exports: walked after the module body runs so
        // any declarations the body added are visible and
        // so re-exports from other modules appear on our
        // exports object. These are pure data copies.
        foreach (var stmt in program.Body)
        {
            if (stmt is ExportNamedDeclaration enx && enx.Source is not null)
            {
                var dep = ImportModule(enx.Source, r.Url);
                foreach (var spec in enx.Specifiers)
                {
                    module.Exports.Set(spec.Exported, dep.Exports.Get(spec.Local));
                }
            }
            else if (stmt is ExportAllDeclaration eax)
            {
                var dep = ImportModule(eax.Source, r.Url);
                if (eax.Namespace is not null)
                {
                    // `export * as ns from './mod'` — wraps
                    // the whole namespace object under a
                    // single name.
                    module.Exports.Set(eax.Namespace, dep.Exports);
                }
                else
                {
                    // `export * from './mod'` — copy every
                    // own enumerable key except `default`
                    // (the spec excludes it from wildcard
                    // re-export).
                    foreach (var key in dep.Exports.OwnKeys())
                    {
                        if (key == "default") continue;
                        module.Exports.Set(key, dep.Exports.Get(key));
                    }
                }
            }
        }

        module.State = ModuleState.Evaluated;
        return module;
    }

    private JsObject MakeTypeError(string message)
    {
        var err = new JsObject { Prototype = TypeErrorPrototype };
        err.Set("message", message);
        return err;
    }
}
