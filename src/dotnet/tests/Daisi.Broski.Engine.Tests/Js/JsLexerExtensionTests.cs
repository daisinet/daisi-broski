using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Tests for the lexer / DOM-globals extensions surfaced by site
/// sweeps: Unicode-escape identifier syntax (used by Angular and
/// some bundlers for internal symbols) and DOM interface stubs
/// (HTMLElement / Element / Node / NodeFilter / CSS).
/// </summary>
public class JsLexerExtensionTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    [Fact]
    public void Unicode_escape_in_identifier_at_start()
    {
        // \u006f is 'o' — `\u006fne` is the same identifier as `one`.
        var r = Eval(@"
            var \u006fne = 1;
            one;
        ");
        Assert.Equal(1.0, r);
    }

    [Fact]
    public void Unicode_escape_in_identifier_mid()
    {
        var r = Eval(@"
            var x\u0079z = 'mid';
            xyz;
        ");
        Assert.Equal("mid", r);
    }

    [Fact]
    public void Unicode_braced_escape_in_identifier()
    {
        var r = Eval(@"
            var \u{006f}ne = 'braced';
            one;
        ");
        Assert.Equal("braced", r);
    }

    [Fact]
    public void Unicode_escape_property_name()
    {
        // The `ɵ` symbol (U+0275) is what Angular uses for
        // internal property names — `service.\u0275prov` reads
        // the property literally named `ɵprov`.
        var r = Eval(@"
            var s = {};
            s['\u0275prov'] = 'ng';
            s.\u0275prov;
        ");
        Assert.Equal("ng", r);
    }

    [Fact]
    public void HTMLElement_global_exists()
    {
        Assert.Equal("function", Eval("typeof HTMLElement;"));
        Assert.Equal("object", Eval("typeof HTMLElement.prototype;"));
    }

    [Fact]
    public void Element_global_exists()
    {
        Assert.Equal("function", Eval("typeof Element;"));
    }

    [Fact]
    public void Node_global_exists()
    {
        Assert.Equal("function", Eval("typeof Node;"));
    }

    [Fact]
    public void HTMLElement_prototype_extension_works()
    {
        // Polyfill pattern: extend HTMLElement.prototype with a
        // shim method.
        var r = Eval(@"
            HTMLElement.prototype.shimmed = function() { return 'ok'; };
            HTMLElement.prototype.shimmed();
        ");
        Assert.Equal("ok", r);
    }

    [Fact]
    public void HTMLElement_construction_throws()
    {
        // `new HTMLElement()` is illegal in real browsers — we
        // mirror that.
        Assert.Throws<JsRuntimeException>(() => Eval("new HTMLElement();"));
    }

    [Fact]
    public void Unicode_letter_identifier_lexes()
    {
        // ASCII-extended letters (ñ, ã, ...) — common in
        // transliteration tables embedded in bundlers.
        var r = Eval(@"
            var o = { ñ: 'n-tilde', ã: 'a-tilde' };
            o.ñ + '|' + o.ã;
        ");
        Assert.Equal("n-tilde|a-tilde", r);
    }
}
