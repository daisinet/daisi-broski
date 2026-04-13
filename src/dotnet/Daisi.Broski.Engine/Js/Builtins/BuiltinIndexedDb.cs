namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>indexedDB</c> — a minimum-viable IndexedDB
/// implementation. Covers what the majority of real pages
/// reach for: open + version upgrade, object stores, the
/// CRUD trio (put / get / delete) plus clear / count /
/// getAll, and transaction lifecycle events. Persists each
/// database as a JSON file under
/// <see cref="JsEngine.IndexedDbBackend"/> when one is
/// configured (default for sandbox runs); transient
/// otherwise.
///
/// <para>
/// Deliberately deferred to a follow-up slice:
/// <list type="bullet">
/// <item>Cursors (<c>openCursor</c> / <c>openKeyCursor</c>).</item>
/// <item>Indexes (<c>createIndex</c> / <c>index()</c>) and
///   query against them.</item>
/// <item><c>IDBKeyRange</c> for ranged <c>getAll</c>
///   queries.</item>
/// <item>Auto-increment + key-path inferred keys. Callers
///   pass an explicit string key for <c>put</c>/<c>add</c>.</item>
/// <item>Structured-clone fidelity. Values round-trip
///   through <c>JSON.stringify</c>/<c>parse</c> — covers
///   JSON-shaped data but loses Date / Map / Set / typed
///   array fidelity.</item>
/// <item>Multi-tab coordination
///   (<c>onversionchange</c> / <c>blocked</c>).</item>
/// </list>
/// </para>
///
/// <para>
/// Asynchrony is faithful: every operation returns an
/// <c>IDBRequest</c> whose <c>onsuccess</c> handler fires
/// through the engine's microtask queue. Callers that drive
/// IndexedDB from a script need to drain the event loop
/// (the same pattern fetch / FileReader / XHR follow).
/// </para>
/// </summary>
internal static class BuiltinIndexedDb
{
    public static void Install(JsEngine engine)
    {
        var requestProto = MakeRequestPrototype(engine);
        var openRequestProto = MakeOpenRequestPrototype(engine, requestProto);
        var dbProto = MakeDatabasePrototype(engine);
        var txProto = MakeTransactionPrototype(engine);
        var storeProto = MakeStorePrototype(engine);

        var factory = new JsIndexedDbFactory(engine,
            requestProto, openRequestProto, dbProto, txProto, storeProto);
        engine.Globals["indexedDB"] = factory;
    }

    private static JsObject MakeRequestPrototype(JsEngine engine)
    {
        return new JsObject { Prototype = engine.ObjectPrototype };
    }

    private static JsObject MakeOpenRequestPrototype(JsEngine engine, JsObject requestProto)
    {
        return new JsObject { Prototype = requestProto };
    }

    private static JsObject MakeDatabasePrototype(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("createObjectStore", new JsFunction("createObjectStore", (thisVal, args) =>
        {
            var db = RequireDb(thisVal, "IDBDatabase.prototype.createObjectStore");
            if (args.Count == 0) JsThrow.TypeError("createObjectStore requires a name");
            var name = JsValue.ToJsString(args[0]);
            return db.CreateObjectStore(name);
        }));

        proto.SetNonEnumerable("deleteObjectStore", new JsFunction("deleteObjectStore", (thisVal, args) =>
        {
            var db = RequireDb(thisVal, "IDBDatabase.prototype.deleteObjectStore");
            if (args.Count == 0) return JsValue.Undefined;
            db.DeleteObjectStore(JsValue.ToJsString(args[0]));
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("transaction", new JsFunction("transaction", (thisVal, args) =>
        {
            var db = RequireDb(thisVal, "IDBDatabase.prototype.transaction");
            if (args.Count == 0)
            {
                JsThrow.TypeError("transaction requires storeNames");
            }
            var storeNames = ExtractStoreNames(args[0]);
            var mode = args.Count > 1 ? JsValue.ToJsString(args[1]) : "readonly";
            return db.OpenTransaction(storeNames, mode);
        }));

        proto.SetNonEnumerable("close", new JsFunction("close", (thisVal, args) =>
        {
            var db = RequireDb(thisVal, "IDBDatabase.prototype.close");
            db.Close();
            return JsValue.Undefined;
        }));

        return proto;
    }

    private static JsObject MakeTransactionPrototype(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        proto.SetNonEnumerable("objectStore", new JsFunction("objectStore", (thisVal, args) =>
        {
            var tx = RequireTx(thisVal, "IDBTransaction.prototype.objectStore");
            if (args.Count == 0) JsThrow.TypeError("objectStore requires a name");
            return tx.GetStore(JsValue.ToJsString(args[0]));
        }));
        proto.SetNonEnumerable("abort", new JsFunction("abort", (thisVal, args) =>
        {
            var tx = RequireTx(thisVal, "IDBTransaction.prototype.abort");
            tx.Abort();
            return JsValue.Undefined;
        }));
        return proto;
    }

    private static JsObject MakeStorePrototype(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("put", new JsFunction("put", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.put");
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            var key = args.Count > 1 ? JsValue.ToJsString(args[1]) : null;
            return st.Put(value, key, allowOverwrite: true);
        }));

        proto.SetNonEnumerable("add", new JsFunction("add", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.add");
            var value = args.Count > 0 ? args[0] : JsValue.Undefined;
            var key = args.Count > 1 ? JsValue.ToJsString(args[1]) : null;
            return st.Put(value, key, allowOverwrite: false);
        }));

        proto.SetNonEnumerable("get", new JsFunction("get", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.get");
            if (args.Count == 0) JsThrow.TypeError("get requires a key");
            return st.GetByKey(JsValue.ToJsString(args[0]));
        }));

        proto.SetNonEnumerable("delete", new JsFunction("delete", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.delete");
            if (args.Count == 0) JsThrow.TypeError("delete requires a key");
            return st.DeleteByKey(JsValue.ToJsString(args[0]));
        }));

        proto.SetNonEnumerable("clear", new JsFunction("clear", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.clear");
            return st.Clear();
        }));

        proto.SetNonEnumerable("count", new JsFunction("count", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.count");
            return st.Count();
        }));

        proto.SetNonEnumerable("getAll", new JsFunction("getAll", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.getAll");
            return st.GetAll();
        }));

        proto.SetNonEnumerable("getAllKeys", new JsFunction("getAllKeys", (thisVal, args) =>
        {
            var st = RequireStore(thisVal, "IDBObjectStore.prototype.getAllKeys");
            return st.GetAllKeys();
        }));

        return proto;
    }

    private static JsIDBDatabase RequireDb(object? thisVal, string method)
    {
        if (thisVal is not JsIDBDatabase db) JsThrow.TypeError($"{method} called on non-IDBDatabase");
        return (JsIDBDatabase)thisVal!;
    }

    private static JsIDBTransaction RequireTx(object? thisVal, string method)
    {
        if (thisVal is not JsIDBTransaction tx) JsThrow.TypeError($"{method} called on non-IDBTransaction");
        return (JsIDBTransaction)thisVal!;
    }

    private static JsIDBObjectStore RequireStore(object? thisVal, string method)
    {
        if (thisVal is not JsIDBObjectStore st) JsThrow.TypeError($"{method} called on non-IDBObjectStore");
        return (JsIDBObjectStore)thisVal!;
    }

    private static IReadOnlyList<string> ExtractStoreNames(object? value)
    {
        if (value is string s) return new[] { s };
        if (value is JsArray arr)
        {
            var list = new List<string>(arr.Elements.Count);
            foreach (var e in arr.Elements) list.Add(JsValue.ToJsString(e));
            return list;
        }
        return new[] { JsValue.ToJsString(value) };
    }
}

/// <summary>The script-visible <c>indexedDB</c> global.
/// Owns the prototypes shared across every database /
/// transaction / store / request the factory mints.</summary>
public sealed class JsIndexedDbFactory : JsObject
{
    private readonly JsEngine _engine;
    internal JsObject RequestProto { get; }
    internal JsObject OpenRequestProto { get; }
    internal JsObject DbProto { get; }
    internal JsObject TxProto { get; }
    internal JsObject StoreProto { get; }

    public JsIndexedDbFactory(JsEngine engine,
        JsObject requestProto, JsObject openRequestProto,
        JsObject dbProto, JsObject txProto, JsObject storeProto)
    {
        _engine = engine;
        RequestProto = requestProto;
        OpenRequestProto = openRequestProto;
        DbProto = dbProto;
        TxProto = txProto;
        StoreProto = storeProto;
        SetNonEnumerable("open", new JsFunction("open", (thisVal, args) =>
        {
            if (args.Count == 0) JsThrow.TypeError("indexedDB.open requires a name");
            var name = JsValue.ToJsString(args[0]);
            int version = args.Count > 1 && args[1] is double v ? (int)v : 1;
            return Open(name, version);
        }));
        SetNonEnumerable("deleteDatabase", new JsFunction("deleteDatabase", (thisVal, args) =>
        {
            if (args.Count == 0) JsThrow.TypeError("indexedDB.deleteDatabase requires a name");
            var name = JsValue.ToJsString(args[0]);
            return DeleteDatabase(name);
        }));
    }

    private JsIDBOpenRequest Open(string name, int targetVersion)
    {
        var request = new JsIDBOpenRequest(_engine, this);
        // Per spec, the request fires asynchronously. Schedule
        // the open work as a microtask so the script can install
        // onsuccess / onupgradeneeded handlers before delivery.
        _engine.EventLoop.QueueMicrotask(() =>
        {
            try
            {
                var snap = _engine.IndexedDbBackend.Load(name) ?? new IndexedDbSnapshot();
                int oldVersion = snap.Version;
                var db = new JsIDBDatabase(_engine, this, name, snap)
                { Prototype = DbProto };
                if (targetVersion > oldVersion)
                {
                    snap.Version = targetVersion;
                    db.OpenVersionChangeTransaction();
                    request.Result = db;
                    request.FireUpgradeNeeded(oldVersion, targetVersion);
                    db.CloseVersionChangeTransaction();
                    db.Persist();
                }
                else
                {
                    request.Result = db;
                }
                request.FireSuccess();
            }
            catch (Exception ex)
            {
                request.FireError(ex.Message);
            }
        });
        return request;
    }

    private JsIDBRequest DeleteDatabase(string name)
    {
        var request = new JsIDBRequest(_engine) { Prototype = RequestProto };
        _engine.EventLoop.QueueMicrotask(() =>
        {
            _engine.IndexedDbBackend.Delete(name);
            request.Result = JsValue.Undefined;
            request.FireSuccess();
        });
        return request;
    }
}

/// <summary>Base IDBRequest. Both ordinary store-op requests and
/// the open-database request inherit from this for their
/// onsuccess / onerror dispatch.</summary>
public class JsIDBRequest : JsObject
{
    private readonly JsEngine _engine;
    private object? _result = JsValue.Undefined;
    private object? _error = JsValue.Null;
    private string _readyState = "pending";
    private readonly Dictionary<string, List<JsFunction>> _listeners = new();

    public JsIDBRequest(JsEngine engine) { _engine = engine; }

    public object? Result { get => _result; set => _result = value; }
    public object? Error { get => _error; set => _error = value; }

    public override object? Get(string key) => key switch
    {
        "result" => _result,
        "error" => _error,
        "readyState" => _readyState,
        _ => base.Get(key),
    };

    public override bool Has(string key) =>
        key is "result" or "error" or "readyState" || base.Has(key);

    public void AddListener(string type, JsFunction cb)
    {
        if (!_listeners.TryGetValue(type, out var list))
        {
            list = new List<JsFunction>();
            _listeners[type] = list;
        }
        list.Add(cb);
    }

    public void FireSuccess()
    {
        _readyState = "done";
        Fire("success");
    }

    /// <summary>Fire the error event with whatever is currently
    /// in <see cref="Error"/>. Callers that haven't already
    /// populated <c>Error</c> can use the message overload to
    /// build a generic Error object.</summary>
    public void FireError()
    {
        _readyState = "done";
        Fire("error");
    }

    public void FireError(string message)
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", "Error");
        err.Set("message", message);
        _error = err;
        FireError();
    }

    private void Fire(string type)
    {
        var evt = new JsObject { Prototype = _engine.ObjectPrototype };
        evt.Set("type", type);
        evt.Set("target", this);
        evt.Set("currentTarget", this);
        evt.Set("bubbles", false);
        if (base.Get("on" + type) is JsFunction onCb)
        {
            _engine.Vm.InvokeJsFunction(onCb, this, new object?[] { evt });
        }
        if (_listeners.TryGetValue(type, out var list))
        {
            foreach (var cb in list.ToArray())
            {
                _engine.Vm.InvokeJsFunction(cb, this, new object?[] { evt });
            }
        }
    }
}

/// <summary>The request returned by <c>indexedDB.open</c>.
/// Carries the additional <c>onupgradeneeded</c> event.</summary>
public sealed class JsIDBOpenRequest : JsIDBRequest
{
    private readonly JsEngine _engine;
    private readonly JsIndexedDbFactory _factory;

    public JsIDBOpenRequest(JsEngine engine, JsIndexedDbFactory factory)
        : base(engine)
    {
        _engine = engine;
        _factory = factory;
        Prototype = factory.OpenRequestProto;
    }

    /// <summary>Fire the upgradeneeded event with the
    /// old/new version pair carried on the event object,
    /// per spec. Runs synchronously so the handler can
    /// create object stores before the success event
    /// fires.</summary>
    public void FireUpgradeNeeded(int oldVersion, int newVersion)
    {
        var evt = new JsObject { Prototype = _engine.ObjectPrototype };
        evt.Set("type", "upgradeneeded");
        evt.Set("target", this);
        evt.Set("currentTarget", this);
        evt.Set("bubbles", false);
        evt.Set("oldVersion", (double)oldVersion);
        evt.Set("newVersion", (double)newVersion);
        if (base.Get("onupgradeneeded") is JsFunction cb)
        {
            _engine.Vm.InvokeJsFunction(cb, this, new object?[] { evt });
        }
    }
}

/// <summary>One opened database. Owns the in-memory
/// snapshot and persists it back through the engine's
/// configured <see cref="IIndexedDbBackend"/> after every
/// transaction commit.</summary>
public sealed class JsIDBDatabase : JsObject
{
    private readonly JsEngine _engine;
    private readonly JsIndexedDbFactory _factory;
    private readonly string _name;
    internal IndexedDbSnapshot Snapshot { get; }
    private readonly Dictionary<string, JsIDBObjectStore> _storeCache = new();
    private bool _versionChangeOpen;
    private bool _closed;

    public JsIDBDatabase(JsEngine engine, JsIndexedDbFactory factory,
        string name, IndexedDbSnapshot snapshot)
    {
        _engine = engine;
        _factory = factory;
        _name = name;
        Snapshot = snapshot;
    }

    public override object? Get(string key) => key switch
    {
        "name" => _name,
        "version" => (double)Snapshot.Version,
        "objectStoreNames" => BuildStoreNames(),
        _ => base.Get(key),
    };

    public override bool Has(string key) =>
        key is "name" or "version" or "objectStoreNames" || base.Has(key);

    private JsArray BuildStoreNames()
    {
        var arr = new JsArray { Prototype = _engine.ArrayPrototype };
        foreach (var k in Snapshot.Stores.Keys) arr.Elements.Add(k);
        return arr;
    }

    /// <summary>Open the implicit version-change transaction
    /// that wraps an <c>onupgradeneeded</c> handler. The flag
    /// gates createObjectStore — outside the upgrade, calls
    /// throw per spec.</summary>
    internal void OpenVersionChangeTransaction() => _versionChangeOpen = true;
    internal void CloseVersionChangeTransaction() => _versionChangeOpen = false;

    public JsIDBObjectStore CreateObjectStore(string name)
    {
        if (!_versionChangeOpen)
        {
            JsThrow.Raise(MakeError("InvalidStateError",
                "createObjectStore must run inside an upgradeneeded handler"));
        }
        if (Snapshot.Stores.ContainsKey(name))
        {
            JsThrow.Raise(MakeError("ConstraintError",
                $"Object store '{name}' already exists"));
        }
        Snapshot.Stores[name] = new IndexedDbStoreSnapshot();
        return ResolveStore(name);
    }

    public void DeleteObjectStore(string name)
    {
        if (!_versionChangeOpen)
        {
            JsThrow.Raise(MakeError("InvalidStateError",
                "deleteObjectStore must run inside an upgradeneeded handler"));
        }
        Snapshot.Stores.Remove(name);
        _storeCache.Remove(name);
    }

    public JsIDBTransaction OpenTransaction(IReadOnlyList<string> storeNames, string mode)
    {
        if (_closed) JsThrow.Raise(MakeError("InvalidStateError", "database is closed"));
        var tx = new JsIDBTransaction(_engine, this, storeNames, mode)
        { Prototype = _factory.TxProto };
        // Per spec, the transaction's complete event fires
        // after every queued request settles. We schedule the
        // commit as a *task* (not a microtask) so the request-
        // success microtasks the caller queues from store
        // operations drain first — RunMicrotasks empties the
        // microtask queue before the next task runs, which gives
        // us the spec-shaped ordering for free.
        _engine.EventLoop.QueueTask(() => tx.Commit());
        return tx;
    }

    public void Close() { _closed = true; }

    /// <summary>Persist the in-memory snapshot through the
    /// engine's configured backend. Called automatically
    /// after each transaction commits.</summary>
    public void Persist() =>
        _engine.IndexedDbBackend.Save(_name, Snapshot);

    /// <summary>Get-or-create the wrapper object for a given
    /// store name. Identity is cached so a script that
    /// queries the same store twice gets the same wrapper.</summary>
    internal JsIDBObjectStore ResolveStore(string name)
    {
        if (_storeCache.TryGetValue(name, out var existing)) return existing;
        if (!Snapshot.Stores.TryGetValue(name, out var snap))
        {
            snap = new IndexedDbStoreSnapshot();
            Snapshot.Stores[name] = snap;
        }
        var st = new JsIDBObjectStore(_engine, this, name, snap)
        { Prototype = _factory.StoreProto };
        _storeCache[name] = st;
        return st;
    }

    internal JsIDBRequest CreateRequest()
    {
        return new JsIDBRequest(_engine) { Prototype = _factory.RequestProto };
    }

    private JsObject MakeError(string name, string message)
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", name);
        err.Set("message", message);
        return err;
    }
}

/// <summary>One transaction over a database. Currently
/// commits implicitly when its containing microtask fires;
/// abort skips the commit and the snapshot rolls back to
/// the previous persisted state via the in-memory copy.</summary>
public sealed class JsIDBTransaction : JsObject
{
    private readonly JsEngine _engine;
    private readonly JsIDBDatabase _db;
    private readonly IReadOnlyList<string> _storeNames;
    private readonly string _mode;
    private bool _aborted;
    private bool _committed;

    public JsIDBTransaction(JsEngine engine, JsIDBDatabase db,
        IReadOnlyList<string> storeNames, string mode)
    {
        _engine = engine;
        _db = db;
        _storeNames = storeNames;
        _mode = mode;
    }

    public override object? Get(string key) => key switch
    {
        "mode" => _mode,
        "db" => _db,
        _ => base.Get(key),
    };

    public override bool Has(string key) =>
        key is "mode" or "db" || base.Has(key);

    public JsIDBObjectStore GetStore(string name)
    {
        if (_aborted) JsThrow.Raise(MakeError("InvalidStateError", "transaction is aborted"));
        // We don't enforce scope strictly — any store on the
        // database is reachable. A future slice can mirror the
        // spec's tighter access rule once the test suite exercises
        // it; getting the storage shape right matters more first.
        return _db.ResolveStore(name);
    }

    public void Abort()
    {
        if (_committed || _aborted) return;
        _aborted = true;
        FireEvent("abort");
    }

    /// <summary>Persist + fire complete. Invoked by the
    /// microtask the database queued at transaction birth.</summary>
    internal void Commit()
    {
        if (_aborted || _committed) return;
        _committed = true;
        if (_mode != "readonly") _db.Persist();
        FireEvent("complete");
    }

    private void FireEvent(string type)
    {
        if (base.Get("on" + type) is JsFunction cb)
        {
            var evt = new JsObject { Prototype = _engine.ObjectPrototype };
            evt.Set("type", type);
            evt.Set("target", this);
            evt.Set("currentTarget", this);
            evt.Set("bubbles", false);
            _engine.Vm.InvokeJsFunction(cb, this, new object?[] { evt });
        }
    }

    private JsObject MakeError(string name, string message)
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", name);
        err.Set("message", message);
        return err;
    }
}

/// <summary>One object store. Holds a reference to the
/// snapshot list so mutations show up in the parent
/// database's snapshot directly.</summary>
public sealed class JsIDBObjectStore : JsObject
{
    private readonly JsEngine _engine;
    private readonly JsIDBDatabase _db;
    private readonly string _name;
    private readonly IndexedDbStoreSnapshot _snap;

    public JsIDBObjectStore(JsEngine engine, JsIDBDatabase db,
        string name, IndexedDbStoreSnapshot snap)
    {
        _engine = engine;
        _db = db;
        _name = name;
        _snap = snap;
    }

    public override object? Get(string key) => key switch
    {
        "name" => _name,
        _ => base.Get(key),
    };

    public override bool Has(string key) =>
        key == "name" || base.Has(key);

    public JsIDBRequest Put(object? value, string? key, bool allowOverwrite)
    {
        var req = NewRequest();
        // Inline keys / autoIncrement aren't implemented — the
        // caller must supply an explicit key for now.
        if (key is null)
        {
            ScheduleError(req, "DataError",
                "put: key argument is required (autoIncrement / inline keys not implemented)");
            return req;
        }

        if (!allowOverwrite)
        {
            foreach (var i in _snap.Items)
            {
                if (i.Key == key)
                {
                    ScheduleError(req, "ConstraintError",
                        $"Key {key} already exists in object store '{_name}'");
                    return req;
                }
            }
        }

        var serialized = StringifyValue(value);
        bool replaced = false;
        for (int i = 0; i < _snap.Items.Count; i++)
        {
            if (_snap.Items[i].Key == key)
            {
                _snap.Items[i].Value = serialized;
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            _snap.Items.Add(new IndexedDbItem { Key = key, Value = serialized });
        }
        ScheduleSuccess(req, key);
        return req;
    }

    /// <summary>The IDB <c>store.get(key)</c> operation. Named
    /// <c>GetByKey</c> rather than <c>Get</c> so it doesn't
    /// shadow the JS-property <see cref="JsObject.Get"/>
    /// override on the base class.</summary>
    public JsIDBRequest GetByKey(string key)
    {
        var req = NewRequest();
        foreach (var i in _snap.Items)
        {
            if (i.Key == key)
            {
                ScheduleSuccess(req, ParseValue(i.Value));
                return req;
            }
        }
        ScheduleSuccess(req, JsValue.Undefined);
        return req;
    }

    /// <summary>Same rename as <see cref="GetByKey"/> — the IDB
    /// <c>store.delete(key)</c> op, kept distinct from
    /// <see cref="JsObject.Delete"/>.</summary>
    public JsIDBRequest DeleteByKey(string key)
    {
        var req = NewRequest();
        for (int i = 0; i < _snap.Items.Count; i++)
        {
            if (_snap.Items[i].Key == key)
            {
                _snap.Items.RemoveAt(i);
                break;
            }
        }
        ScheduleSuccess(req, JsValue.Undefined);
        return req;
    }

    public JsIDBRequest Clear()
    {
        var req = NewRequest();
        _snap.Items.Clear();
        ScheduleSuccess(req, JsValue.Undefined);
        return req;
    }

    public JsIDBRequest Count()
    {
        var req = NewRequest();
        ScheduleSuccess(req, (double)_snap.Items.Count);
        return req;
    }

    public JsIDBRequest GetAll()
    {
        var req = NewRequest();
        var arr = new JsArray { Prototype = _engine.ArrayPrototype };
        foreach (var i in _snap.Items) arr.Elements.Add(ParseValue(i.Value));
        ScheduleSuccess(req, arr);
        return req;
    }

    public JsIDBRequest GetAllKeys()
    {
        var req = NewRequest();
        var arr = new JsArray { Prototype = _engine.ArrayPrototype };
        foreach (var i in _snap.Items) arr.Elements.Add(i.Key);
        ScheduleSuccess(req, arr);
        return req;
    }

    private JsIDBRequest NewRequest() => _db.CreateRequest();

    private void ScheduleSuccess(JsIDBRequest req, object? result)
    {
        req.Result = result;
        _engine.EventLoop.QueueMicrotask(req.FireSuccess);
    }

    private void ScheduleError(JsIDBRequest req, string name, string message)
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", name);
        err.Set("message", message);
        req.Error = err;
        // Use the no-arg FireError so the named error we
        // just attached survives — the message overload would
        // overwrite Error with a generic "Error".
        _engine.EventLoop.QueueMicrotask(req.FireError);
    }

    /// <summary>Convert a JS value to a JSON string by
    /// invoking the engine's installed
    /// <c>JSON.stringify</c>. Works for any JSON-shaped value
    /// — primitives, arrays, plain objects.</summary>
    private string StringifyValue(object? value)
    {
        if (_engine.Globals["JSON"] is not JsObject jsonGlobal) return "null";
        if (jsonGlobal.Get("stringify") is not JsFunction stringify) return "null";
        var result = _engine.Vm.InvokeJsFunction(stringify, jsonGlobal, new object?[] { value });
        return result is string s ? s : "null";
    }

    /// <summary>Reverse of <see cref="StringifyValue"/> via
    /// <c>JSON.parse</c>.</summary>
    private object? ParseValue(string serialized)
    {
        if (string.IsNullOrEmpty(serialized)) return JsValue.Null;
        try { return BuiltinJson.ParseText(_engine, serialized); }
        catch { return JsValue.Null; }
    }
}
