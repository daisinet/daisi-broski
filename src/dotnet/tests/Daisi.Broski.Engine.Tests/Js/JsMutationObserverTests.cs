using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 5d — real <see cref="MutationDispatcher"/>-backed
/// <c>MutationObserver</c>. Tests verify spec-shaped record
/// delivery for childList / attributes / characterData
/// mutations, microtask batching, subtree scoping,
/// attributeFilter, attributeOldValue, takeRecords, and
/// disconnect.
/// </summary>
public class JsMutationObserverTests
{
    private static JsEngine NewEngine()
    {
        var eng = new JsEngine();
        // Attach an empty document so observer.observe(document.body)
        // is reachable.
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        doc.AppendChild(html);
        html.AppendChild(body);
        eng.AttachDocument(doc, new Uri("https://example.com/"));
        return eng;
    }

    [Fact]
    public void Observer_constructor_requires_callback()
    {
        var eng = NewEngine();
        Assert.Throws<JsRuntimeException>(() =>
            eng.Evaluate("new MutationObserver();"));
    }

    [Fact]
    public void ChildList_mutation_delivers_one_record()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var records;
            var obs = new MutationObserver(function (mutations) { records = mutations; });
            obs.observe(document.body, { childList: true });
            var p = document.createElement('p');
            document.body.appendChild(p);
        ");
        eng.DrainEventLoop();

        Assert.Equal(1.0, eng.Evaluate("records.length;"));
        Assert.Equal("childList", eng.Evaluate("records[0].type;"));
        Assert.Equal(1.0, eng.Evaluate("records[0].addedNodes.length;"));
        Assert.Equal(0.0, eng.Evaluate("records[0].removedNodes.length;"));
        Assert.Equal("P", eng.Evaluate("records[0].addedNodes[0].tagName;"));
    }

    [Fact]
    public void Multiple_mutations_batch_into_one_callback()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var calls = 0;
            var totalRecords = 0;
            var obs = new MutationObserver(function (mutations) {
                calls++;
                totalRecords += mutations.length;
            });
            obs.observe(document.body, { childList: true });
            for (var i = 0; i < 5; i++) {
                document.body.appendChild(document.createElement('span'));
            }
        ");
        eng.DrainEventLoop();
        Assert.Equal(1.0, eng.Evaluate("calls;"));
        Assert.Equal(5.0, eng.Evaluate("totalRecords;"));
    }

    [Fact]
    public void Attribute_mutation_records_name_and_old_value()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var rec;
            var obs = new MutationObserver(function (mutations) { rec = mutations[0]; });
            document.body.setAttribute('data-x', 'first');
            obs.observe(document.body, { attributes: true, attributeOldValue: true });
            document.body.setAttribute('data-x', 'second');
        ");
        eng.DrainEventLoop();
        Assert.Equal("attributes", eng.Evaluate("rec.type;"));
        Assert.Equal("data-x", eng.Evaluate("rec.attributeName;"));
        Assert.Equal("first", eng.Evaluate("rec.oldValue;"));
    }

    [Fact]
    public void AttributeFilter_skips_mutations_outside_the_filter()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var fired = 0;
            var obs = new MutationObserver(function (mutations) { fired += mutations.length; });
            obs.observe(document.body, { attributes: true, attributeFilter: ['data-keep'] });
            document.body.setAttribute('data-other', '1');
            document.body.setAttribute('data-keep', '2');
            document.body.setAttribute('data-other', '3');
        ");
        eng.DrainEventLoop();
        Assert.Equal(1.0, eng.Evaluate("fired;"));
    }

    [Fact]
    public void CharacterData_mutation_fires_with_oldValue()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var t = document.createTextNode('hello');
            document.body.appendChild(t);
            var rec;
            var obs = new MutationObserver(function (mutations) { rec = mutations[0]; });
            obs.observe(t, { characterData: true, characterDataOldValue: true });
            t.data = 'world';
        ");
        eng.DrainEventLoop();
        Assert.Equal("characterData", eng.Evaluate("rec.type;"));
        Assert.Equal("hello", eng.Evaluate("rec.oldValue;"));
    }

    [Fact]
    public void Subtree_observation_catches_descendant_mutations()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var seen = 0;
            var obs = new MutationObserver(function (mutations) { seen += mutations.length; });
            obs.observe(document.body, { childList: true, subtree: true });
            var inner = document.createElement('div');
            document.body.appendChild(inner);
            inner.appendChild(document.createElement('span'));
        ");
        eng.DrainEventLoop();
        Assert.Equal(2.0, eng.Evaluate("seen;"));
    }

    [Fact]
    public void NoSubtree_does_not_catch_descendant_mutations()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var seen = 0;
            var obs = new MutationObserver(function (mutations) { seen += mutations.length; });
            var inner = document.createElement('div');
            document.body.appendChild(inner);
            obs.observe(document.body, { childList: true });
            // Mutation in `inner` (descendant) — should NOT fire
            // because subtree is false.
            inner.appendChild(document.createElement('span'));
        ");
        eng.DrainEventLoop();
        Assert.Equal(0.0, eng.Evaluate("seen;"));
    }

    [Fact]
    public void RemoveChild_records_a_removedNodes_record()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var p = document.createElement('p');
            document.body.appendChild(p);
            var rec;
            var obs = new MutationObserver(function (mutations) { rec = mutations[0]; });
            obs.observe(document.body, { childList: true });
            document.body.removeChild(p);
        ");
        eng.DrainEventLoop();
        Assert.Equal(0.0, eng.Evaluate("rec.addedNodes.length;"));
        Assert.Equal(1.0, eng.Evaluate("rec.removedNodes.length;"));
        Assert.Equal("P", eng.Evaluate("rec.removedNodes[0].tagName;"));
    }

    [Fact]
    public void Disconnect_stops_record_delivery()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var fired = 0;
            var obs = new MutationObserver(function (m) { fired += m.length; });
            obs.observe(document.body, { childList: true });
            obs.disconnect();
            document.body.appendChild(document.createElement('p'));
        ");
        eng.DrainEventLoop();
        Assert.Equal(0.0, eng.Evaluate("fired;"));
    }

    [Fact]
    public void TakeRecords_returns_pending_records_synchronously()
    {
        var eng = NewEngine();
        // No DrainEventLoop — takeRecords should pull records that
        // are queued but haven't fired yet.
        eng.Evaluate(@"
            var fired = 0;
            var obs = new MutationObserver(function (m) { fired += m.length; });
            obs.observe(document.body, { childList: true });
            document.body.appendChild(document.createElement('p'));
            document.body.appendChild(document.createElement('p'));
            var taken = obs.takeRecords();
        ");
        Assert.Equal(2.0, eng.Evaluate("taken.length;"));
        // Drain — callback should NOT fire because takeRecords
        // emptied the queue.
        eng.DrainEventLoop();
        Assert.Equal(0.0, eng.Evaluate("fired;"));
    }

    [Fact]
    public void PreviousSibling_and_nextSibling_fields_populate()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var first = document.createElement('p');
            var third = document.createElement('p');
            document.body.appendChild(first);
            document.body.appendChild(third);
            var rec;
            var obs = new MutationObserver(function (m) { rec = m[0]; });
            obs.observe(document.body, { childList: true });
            var middle = document.createElement('span');
            document.body.insertBefore(middle, third);
        ");
        eng.DrainEventLoop();
        Assert.Equal("P", eng.Evaluate("rec.previousSibling.tagName;"));
        Assert.Equal("P", eng.Evaluate("rec.nextSibling.tagName;"));
    }

    [Fact]
    public void OldValue_omitted_when_attributeOldValue_is_false()
    {
        var eng = NewEngine();
        eng.Evaluate(@"
            var rec;
            var obs = new MutationObserver(function (m) { rec = m[0]; });
            document.body.setAttribute('x', 'first');
            obs.observe(document.body, { attributes: true });
            document.body.setAttribute('x', 'second');
        ");
        eng.DrainEventLoop();
        Assert.Equal(JsValue.Null, eng.Evaluate("rec.oldValue;"));
    }
}
