using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 5f — minimum-viable IndexedDB. Covers the open +
/// upgrade flow, CRUD on object stores (put / add / get /
/// delete / clear / count / getAll / getAllKeys), transaction
/// lifecycle events, persistence to disk via the file backend,
/// and version upgrades that re-fire upgradeneeded.
/// </summary>
public class JsIndexedDbTests : IDisposable
{
    private readonly string _tempDir;

    public JsIndexedDbTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "daisi-broski-idb-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    private static JsEngine NewEngine() => new JsEngine();

    private JsEngine NewPersistentEngine()
    {
        var eng = new JsEngine();
        eng.IndexedDbBackend = new FileIndexedDbBackend(_tempDir);
        return eng;
    }

    [Fact]
    public void IndexedDB_global_exists()
    {
        Assert.Equal("object", NewEngine().Evaluate("typeof indexedDB;"));
    }

    [Fact]
    public void Open_fires_upgradeneeded_then_success_on_first_open()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var events = [];
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function (e) {
                events.push('upgrade:' + e.oldVersion + '->' + e.newVersion);
            };
            req.onsuccess = function () { events.push('success'); };
        ");
        eng.DrainEventLoop();
        Assert.Equal("upgrade:0->1,success", eng.Evaluate("events.join(',');"));
    }

    [Fact]
    public void CreateObjectStore_only_inside_upgradeneeded()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var db;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { db = req.result; db.createObjectStore('items'); };
            req.onsuccess = function () { db = req.result; };
        ");
        eng.DrainEventLoop();
        Assert.Equal(
            "InvalidStateError",
            eng.Evaluate(@"
                var name;
                try { db.createObjectStore('late'); } catch (e) { name = e.name; }
                name;
            "));
    }

    [Fact]
    public void Put_then_get_round_trips_object_value()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var got;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var db = req.result;
                var tx = db.transaction('items', 'readwrite');
                var store = tx.objectStore('items');
                store.put({a:1, b:'two'}, 'k1');
                var g = store.get('k1');
                g.onsuccess = function () { got = g.result; };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal(1.0, eng.Evaluate("got.a;"));
        Assert.Equal("two", eng.Evaluate("got.b;"));
    }

    [Fact]
    public void Get_missing_key_resolves_undefined()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var got = 'sentinel';
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                var g = tx.objectStore('items').get('nope');
                g.onsuccess = function () { got = g.result; };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal(JsValue.Undefined, eng.Evaluate("got;"));
    }

    [Fact]
    public void Add_rejects_duplicate_keys_via_onerror()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var errName;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                var s = tx.objectStore('items');
                s.add('first', 'k');
                var dup = s.add('second', 'k');
                dup.onerror = function () { errName = dup.error.name; };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal("ConstraintError", eng.Evaluate("errName;"));
    }

    [Fact]
    public void Delete_removes_a_key()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var n;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                var s = tx.objectStore('items');
                s.put('v', 'k');
                s.delete('k');
                var c = s.count();
                c.onsuccess = function () { n = c.result; };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal(0.0, eng.Evaluate("n;"));
    }

    [Fact]
    public void GetAll_and_getAllKeys_return_arrays()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var values, keys;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                var s = tx.objectStore('items');
                s.put('a', 'k1');
                s.put('b', 'k2');
                s.put('c', 'k3');
                var gv = s.getAll();
                gv.onsuccess = function () { values = gv.result; };
                var gk = s.getAllKeys();
                gk.onsuccess = function () { keys = gk.result; };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal("a,b,c", eng.Evaluate("values.join(',');"));
        Assert.Equal("k1,k2,k3", eng.Evaluate("keys.join(',');"));
    }

    [Fact]
    public void Clear_empties_the_store()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var n;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                var s = tx.objectStore('items');
                s.put('a', 'k1');
                s.put('b', 'k2');
                s.clear();
                var c = s.count();
                c.onsuccess = function () { n = c.result; };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal(0.0, eng.Evaluate("n;"));
    }

    [Fact]
    public void Transaction_complete_fires_after_requests()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var order = [];
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                tx.oncomplete = function () { order.push('complete'); };
                var p = tx.objectStore('items').put('v', 'k');
                p.onsuccess = function () { order.push('put-success'); };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal("put-success,complete", eng.Evaluate("order.join(',');"));
    }

    [Fact]
    public void Snapshots_persist_across_engines_with_file_backend()
    {
        // First engine: write some data.
        var a = NewPersistentEngine();
        a.Evaluate(@"
            var req = indexedDB.open('mydb', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                tx.objectStore('items').put('persisted', 'k');
            };
        ");
        a.DrainEventLoop();

        // Second engine pointed at the same backend dir reads it back.
        var b = NewPersistentEngine();
        b.Evaluate(@"
            var got;
            var req = indexedDB.open('mydb', 1);
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readonly');
                var g = tx.objectStore('items').get('k');
                g.onsuccess = function () { got = g.result; };
            };
        ");
        b.DrainEventLoop();
        Assert.Equal("persisted", b.Evaluate("got;"));
    }

    [Fact]
    public void Reopen_with_higher_version_fires_upgradeneeded_again()
    {
        var a = NewPersistentEngine();
        a.Evaluate(@"
            var req = indexedDB.open('versioned', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('s1'); };
        ");
        a.DrainEventLoop();

        var b = NewPersistentEngine();
        b.Evaluate(@"
            var info;
            var req = indexedDB.open('versioned', 2);
            req.onupgradeneeded = function (e) {
                info = e.oldVersion + '->' + e.newVersion;
                req.result.createObjectStore('s2');
            };
        ");
        b.DrainEventLoop();
        Assert.Equal("1->2", b.Evaluate("info;"));
    }

    [Fact]
    public void DeleteDatabase_drops_persisted_state()
    {
        var a = NewPersistentEngine();
        a.Evaluate(@"
            var req = indexedDB.open('todelete', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('s'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('s', 'readwrite');
                tx.objectStore('s').put('v', 'k');
            };
        ");
        a.DrainEventLoop();

        // Confirm file exists, then delete and confirm gone.
        var beforeFiles = Directory.GetFiles(_tempDir, "*.json").Length;
        Assert.True(beforeFiles >= 1);

        a.Evaluate("indexedDB.deleteDatabase('todelete');");
        a.DrainEventLoop();
        var afterFiles = Directory.GetFiles(_tempDir, "*.json").Length;
        Assert.Equal(beforeFiles - 1, afterFiles);
    }

    [Fact]
    public void ObjectStoreNames_lists_created_stores()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var names;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () {
                var db = req.result;
                db.createObjectStore('first');
                db.createObjectStore('second');
            };
            req.onsuccess = function () { names = req.result.objectStoreNames; };
        ");
        eng.DrainEventLoop();
        Assert.Equal("first,second", eng.Evaluate("names.join(',');"));
    }

    [Fact]
    public void DeleteObjectStore_removes_store_during_upgrade()
    {
        var a = NewPersistentEngine();
        a.Evaluate(@"
            var req = indexedDB.open('drop', 1);
            req.onupgradeneeded = function () {
                var db = req.result;
                db.createObjectStore('keep');
                db.createObjectStore('toss');
            };
        ");
        a.DrainEventLoop();

        var b = NewPersistentEngine();
        b.Evaluate(@"
            var names;
            var req = indexedDB.open('drop', 2);
            req.onupgradeneeded = function () { req.result.deleteObjectStore('toss'); };
            req.onsuccess = function () { names = req.result.objectStoreNames; };
        ");
        b.DrainEventLoop();
        Assert.Equal("keep", b.Evaluate("names.join(',');"));
    }

    [Fact]
    public void Put_without_explicit_key_returns_DataError()
    {
        // v1 doesn't implement autoIncrement / inline keys, so
        // an explicit key is mandatory. Surface the limitation
        // as a clear DataError rather than silently writing
        // under an opaque key.
        var eng = NewEngine();
        eng.Evaluate(@"
            var name;
            var req = indexedDB.open('test', 1);
            req.onupgradeneeded = function () { req.result.createObjectStore('items'); };
            req.onsuccess = function () {
                var tx = req.result.transaction('items', 'readwrite');
                var p = tx.objectStore('items').put('v');
                p.onerror = function () { name = p.error.name; };
            };
        ");
        eng.DrainEventLoop();
        Assert.Equal("DataError", eng.Evaluate("name;"));
    }
}
