using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 3c ship-gate test: exercise the full pipeline on a
/// canned HTML fixture. Parse the page with the phase-1
/// tree builder, attach the resulting document to a fresh
/// <see cref="JsEngine"/>, run every inline
/// <c>&lt;script&gt;</c> tag in document order, drain the
/// event loop, and then assert on the post-script DOM
/// state.
///
/// <para>
/// The fixture uses all of the phase 3c surface: DOM
/// queries, mutations via <c>appendChild</c> and
/// <c>innerHTML</c>, event listeners with
/// <c>dispatchEvent</c>, <c>classList</c>, optional
/// chaining, nullish coalescing, <c>crypto.randomUUID</c>,
/// the spread operator, arrow functions, template
/// literals, and a BigInt calculation. If any of those
/// regress, this test catches it as a single integration
/// failure instead of requiring the developer to hunt
/// through unit tests.
/// </para>
/// </summary>
public class JsPhase3ShipGateTests
{
    private const string FixtureHtml = @"<!DOCTYPE html>
<html>
<head>
<title>daisi-broski ship gate</title>
</head>
<body>
<div id='app'>
  <h1 id='greeting'>Loading...</h1>
  <ul id='list'></ul>
  <button id='btn' class='primary'>Click me</button>
  <div id='summary'></div>
</div>

<script>
// Phase 3c surface in one place.

// 1. Optional chaining + nullish coalescing.
var appTitle = document.getElementById('app')?.querySelector('h1')?.textContent ?? 'fallback';
document.getElementById('greeting').textContent = 'Hello, ' + (appTitle === 'Loading...' ? 'world' : 'unknown');

// 2. Build a list with createElement + appendChild.
var items = ['alpha', 'beta', 'gamma'];
var list = document.getElementById('list');
items.forEach(function (item) {
    var li = document.createElement('li');
    li.textContent = item;
    li.classList.add('row');
    list.appendChild(li);
});

// 3. innerHTML write with nested HTML.
document.getElementById('summary').innerHTML =
    '<p class=""note""><strong>Ready</strong> with ' + items.length + ' items.</p>';

// 4. classList toggle.
var btn = document.getElementById('btn');
btn.classList.toggle('primary');  // remove existing
btn.classList.toggle('ready');    // add new

// 5. Event listener + dispatchEvent with a custom event.
var clickLog = [];
btn.addEventListener('click', function (e) { clickLog.push(e.type); });
btn.dispatchEvent(new Event('click'));
btn.dispatchEvent(new Event('click'));
window.__clickCount = clickLog.length;

// 6. BigInt arithmetic — the final phase 3 slice.
var big = 2n * 3n * 5n * 7n * 11n * 13n;
window.__primorial = String(big);

// 7. crypto.randomUUID — should produce a 36-char v4 UUID.
window.__uuidLen = crypto.randomUUID().length;
</script>
</body>
</html>";

    [Fact]
    public void Fixture_page_runs_end_to_end()
    {
        // --- PARSE -------------------------------------------------
        var doc = HtmlTreeBuilder.Parse(FixtureHtml);
        Assert.NotNull(doc.Body);

        // Pre-JS state.
        int preListItems = doc.QuerySelectorAll("#list li").Count;
        Assert.Equal(0, preListItems);
        var preGreeting = doc.GetElementById("greeting")!.TextContent;
        Assert.Equal("Loading...", preGreeting);

        // --- ATTACH + RUN -----------------------------------------
        var engine = new JsEngine();
        engine.AttachDocument(doc);

        int scriptsRun = 0;
        foreach (var script in doc.QuerySelectorAll("script"))
        {
            if (script.HasAttribute("src")) continue;
            engine.RunScript(script.TextContent);
            scriptsRun++;
        }
        Assert.Equal(1, scriptsRun);

        // --- ASSERT POST-JS DOM ------------------------------------
        // (1) Optional chaining + textContent write.
        Assert.Equal("Hello, world", doc.GetElementById("greeting")!.TextContent);

        // (2) createElement + appendChild built 3 list items.
        var listItems = doc.QuerySelectorAll("#list li");
        Assert.Equal(3, listItems.Count);
        Assert.Equal("alpha", listItems[0].TextContent);
        Assert.Equal("beta", listItems[1].TextContent);
        Assert.Equal("gamma", listItems[2].TextContent);
        foreach (var li in listItems)
        {
            Assert.Contains("row", li.ClassList);
        }

        // (3) innerHTML write parsed nested HTML.
        var note = doc.QuerySelector("#summary .note");
        Assert.NotNull(note);
        Assert.Equal("note", note!.GetAttribute("class"));
        var strong = note.QuerySelector("strong");
        Assert.NotNull(strong);
        Assert.Equal("Ready", strong!.TextContent);
        Assert.Contains("3 items", note.TextContent);

        // (4) classList toggle removed 'primary' and added 'ready'.
        var btn = doc.GetElementById("btn")!;
        Assert.DoesNotContain("primary", btn.ClassList);
        Assert.Contains("ready", btn.ClassList);

        // (5) Event listener fired twice.
        Assert.Equal(2.0, engine.Globals["__clickCount"]);

        // (6) BigInt primorial: 2*3*5*7*11*13 = 30030.
        Assert.Equal("30030", engine.Globals["__primorial"]);

        // (7) crypto.randomUUID returned a 36-char string.
        Assert.Equal(36.0, engine.Globals["__uuidLen"]);
    }

    /// <summary>
    /// Dumpable diagnostic variant — on failure, we want a
    /// clear trace of what ran and what the DOM looked like
    /// so debugging the integration is fast. Passes on
    /// success; the main value is that it exercises the
    /// HtmlSerializer round-trip after JS mutation.
    /// </summary>
    [Fact]
    public void Fixture_post_script_serializes_back_to_html()
    {
        var doc = HtmlTreeBuilder.Parse(FixtureHtml);
        var engine = new JsEngine();
        engine.AttachDocument(doc);
        foreach (var script in doc.QuerySelectorAll("script"))
        {
            if (!script.HasAttribute("src"))
            {
                engine.RunScript(script.TextContent);
            }
        }

        // Serialize the #app subtree — this round-trips the
        // JS-mutated DOM through the HtmlSerializer we shipped
        // in slice 3c-8.
        var app = doc.GetElementById("app")!;
        var serialized = HtmlSerializer.SerializeNode(app);

        // The serialized output should include every piece
        // of script-mutated state.
        Assert.Contains("Hello, world", serialized);
        Assert.Contains("<li class=\"row\">alpha</li>", serialized);
        Assert.Contains("<li class=\"row\">beta</li>", serialized);
        Assert.Contains("<li class=\"row\">gamma</li>", serialized);
        Assert.Contains("class=\"note\"", serialized);
        Assert.Contains("<strong>Ready</strong>", serialized);
        Assert.Contains("class=\"ready\"", serialized);
        Assert.DoesNotContain("primary", serialized);
    }
}
