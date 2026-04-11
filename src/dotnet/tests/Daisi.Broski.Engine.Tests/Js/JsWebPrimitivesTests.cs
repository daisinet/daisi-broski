using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-3: the WHATWG web-primitive built-ins —
/// <c>URL</c>, <c>URLSearchParams</c>, <c>TextEncoder</c>,
/// <c>TextDecoder</c>, <c>atob</c>, and <c>btoa</c>. These
/// are the foundation primitives <c>fetch</c> will build on
/// in a follow-up slice; each is small, self-contained, and
/// individually useful, so they ship together.
/// </summary>
public class JsWebPrimitivesTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // URL
    // ========================================================

    [Fact]
    public void Url_parses_absolute_http()
    {
        Assert.Equal(
            "https://example.com/path",
            Eval("new URL('https://example.com/path').href;"));
    }

    [Fact]
    public void Url_protocol_includes_trailing_colon()
    {
        Assert.Equal("https:", Eval("new URL('https://example.com').protocol;"));
    }

    [Fact]
    public void Url_hostname_excludes_port()
    {
        Assert.Equal(
            "example.com",
            Eval("new URL('https://example.com:8080/x').hostname;"));
    }

    [Fact]
    public void Url_host_includes_port_when_non_default()
    {
        Assert.Equal(
            "example.com:8080",
            Eval("new URL('https://example.com:8080/x').host;"));
    }

    [Fact]
    public void Url_port_is_empty_for_default_port()
    {
        Assert.Equal(
            "",
            Eval("new URL('https://example.com/x').port;"));
    }

    [Fact]
    public void Url_pathname_defaults_to_slash()
    {
        Assert.Equal(
            "/",
            Eval("new URL('https://example.com').pathname;"));
    }

    [Fact]
    public void Url_search_includes_leading_question()
    {
        Assert.Equal(
            "?a=1&b=2",
            Eval("new URL('https://example.com/x?a=1&b=2').search;"));
    }

    [Fact]
    public void Url_hash_includes_leading_pound()
    {
        Assert.Equal(
            "#section",
            Eval("new URL('https://example.com/x#section').hash;"));
    }

    [Fact]
    public void Url_origin_for_http()
    {
        Assert.Equal(
            "https://example.com",
            Eval("new URL('https://example.com/a/b/c').origin;"));
    }

    [Fact]
    public void Url_toString_returns_href()
    {
        Assert.Equal(
            "https://example.com/",
            Eval("new URL('https://example.com/').toString();"));
    }

    [Fact]
    public void Url_resolves_relative_against_base()
    {
        Assert.Equal(
            "https://example.com/a/c",
            Eval("new URL('c', 'https://example.com/a/b').href;"));
    }

    [Fact]
    public void Url_invalid_throws_TypeError()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { new URL('not a url'); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void Url_pathname_mutation_updates_href()
    {
        Assert.Equal(
            "https://example.com/new/path",
            Eval(@"
                var u = new URL('https://example.com/old');
                u.pathname = '/new/path';
                u.href;
            "));
    }

    [Fact]
    public void Url_search_mutation_updates_href()
    {
        Assert.Equal(
            "https://example.com/?x=1",
            Eval(@"
                var u = new URL('https://example.com/');
                u.search = '?x=1';
                u.href;
            "));
    }

    // ========================================================
    // URLSearchParams
    // ========================================================

    [Fact]
    public void SearchParams_parses_string_input()
    {
        Assert.Equal(
            "1",
            Eval("new URLSearchParams('a=1&b=2').get('a');"));
    }

    [Fact]
    public void SearchParams_ignores_leading_question()
    {
        Assert.Equal(
            "1",
            Eval("new URLSearchParams('?a=1').get('a');"));
    }

    [Fact]
    public void SearchParams_has_returns_true_for_present_key()
    {
        Assert.Equal(
            true,
            Eval("new URLSearchParams('x=1').has('x');"));
    }

    [Fact]
    public void SearchParams_has_returns_false_for_missing()
    {
        Assert.Equal(
            false,
            Eval("new URLSearchParams('x=1').has('y');"));
    }

    [Fact]
    public void SearchParams_getAll_returns_all_matches()
    {
        Assert.Equal(
            2.0,
            Eval("new URLSearchParams('x=1&x=2').getAll('x').length;"));
    }

    [Fact]
    public void SearchParams_set_replaces_all()
    {
        Assert.Equal(
            "x=new",
            Eval(@"
                var p = new URLSearchParams('x=1&x=2');
                p.set('x', 'new');
                p.toString();
            "));
    }

    [Fact]
    public void SearchParams_append_adds_duplicate()
    {
        Assert.Equal(
            "a=1&a=2",
            Eval(@"
                var p = new URLSearchParams();
                p.append('a', '1');
                p.append('a', '2');
                p.toString();
            "));
    }

    [Fact]
    public void SearchParams_delete_removes_all_matches()
    {
        Assert.Equal(
            "y=2",
            Eval(@"
                var p = new URLSearchParams('x=1&y=2&x=3');
                p.delete('x');
                p.toString();
            "));
    }

    [Fact]
    public void SearchParams_size_reflects_entry_count()
    {
        Assert.Equal(
            3.0,
            Eval("new URLSearchParams('a=1&b=2&c=3').size;"));
    }

    [Fact]
    public void SearchParams_encodes_special_characters()
    {
        // Space → +, & → %26
        Assert.Equal(
            "q=hello+world&filter=%26",
            Eval(@"
                var p = new URLSearchParams();
                p.append('q', 'hello world');
                p.append('filter', '&');
                p.toString();
            "));
    }

    [Fact]
    public void SearchParams_decodes_percent_escapes()
    {
        Assert.Equal(
            "hello world",
            Eval("new URLSearchParams('q=hello+world').get('q');"));
    }

    [Fact]
    public void SearchParams_is_iterable()
    {
        Assert.Equal(
            "a=1,b=2,c=3",
            Eval(@"
                var p = new URLSearchParams('a=1&b=2&c=3');
                var out = [];
                for (var pair of p) { out.push(pair[0] + '=' + pair[1]); }
                out.join(',');
            "));
    }

    [Fact]
    public void SearchParams_forEach_visits_each_pair()
    {
        Assert.Equal(
            "a:1|b:2",
            Eval(@"
                var p = new URLSearchParams('a=1&b=2');
                var out = [];
                p.forEach(function (v, k) { out.push(k + ':' + v); });
                out.join('|');
            "));
    }

    [Fact]
    public void Url_searchParams_is_live_view()
    {
        Assert.Equal(
            "https://example.com/?a=1&b=2",
            Eval(@"
                var u = new URL('https://example.com/?a=1');
                u.searchParams.append('b', '2');
                u.href;
            "));
    }

    [Fact]
    public void Url_searchParams_same_instance_across_reads()
    {
        Assert.Equal(
            true,
            Eval(@"
                var u = new URL('https://example.com/?a=1');
                u.searchParams === u.searchParams;
            "));
    }

    // ========================================================
    // TextEncoder / TextDecoder
    // ========================================================

    [Fact]
    public void TextEncoder_encodes_ascii()
    {
        Assert.Equal(
            5.0,
            Eval("new TextEncoder().encode('hello').length;"));
    }

    [Fact]
    public void TextEncoder_encodes_utf8_multibyte()
    {
        // "é" is 2 bytes in UTF-8 (0xC3 0xA9)
        Assert.Equal(
            2.0,
            Eval("new TextEncoder().encode('é').length;"));
    }

    [Fact]
    public void TextEncoder_encoding_property_is_utf8()
    {
        Assert.Equal(
            "utf-8",
            Eval("new TextEncoder().encoding;"));
    }

    [Fact]
    public void TextDecoder_round_trips_ascii()
    {
        Assert.Equal(
            "hello world",
            Eval(@"
                var enc = new TextEncoder();
                var dec = new TextDecoder();
                dec.decode(enc.encode('hello world'));
            "));
    }

    [Fact]
    public void TextDecoder_round_trips_utf8()
    {
        Assert.Equal(
            "café 🎉",
            Eval(@"
                var enc = new TextEncoder();
                var dec = new TextDecoder();
                dec.decode(enc.encode('café 🎉'));
            "));
    }

    [Fact]
    public void TextDecoder_defaults_to_utf8()
    {
        Assert.Equal(
            "utf-8",
            Eval("new TextDecoder().encoding;"));
    }

    [Fact]
    public void TextDecoder_accepts_utf8_label()
    {
        // Should not throw.
        Assert.Equal(
            "utf-8",
            Eval("new TextDecoder('utf-8').encoding;"));
    }

    [Fact]
    public void TextDecoder_rejects_unknown_encoding()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { new TextDecoder('shift_jis'); false; }
                catch (e) { e instanceof RangeError; }
            "));
    }

    [Fact]
    public void TextEncoder_output_is_uint8array()
    {
        Assert.Equal(
            true,
            Eval(@"
                var enc = new TextEncoder();
                var bytes = enc.encode('abc');
                bytes instanceof Uint8Array;
            "));
    }

    // ========================================================
    // atob / btoa
    // ========================================================

    [Fact]
    public void Btoa_encodes_ascii()
    {
        Assert.Equal("aGVsbG8=", Eval("btoa('hello');"));
    }

    [Fact]
    public void Btoa_encodes_empty_string()
    {
        Assert.Equal("", Eval("btoa('');"));
    }

    [Fact]
    public void Atob_decodes_ascii()
    {
        Assert.Equal("hello", Eval("atob('aGVsbG8=');"));
    }

    [Fact]
    public void Btoa_throws_on_non_latin1()
    {
        // '€' is U+20AC — outside the 0x00–0xFF range that
        // btoa's binary-string contract allows.
        Assert.Equal(
            true,
            Eval(@"
                try { btoa('€'); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void Atob_throws_on_invalid_base64()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { atob('!!!not base64!!!'); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void Atob_strips_whitespace()
    {
        Assert.Equal("hello", Eval("atob('aGVs bG8=');"));
    }

    [Fact]
    public void Base64_round_trip()
    {
        Assert.Equal(
            "The quick brown fox",
            Eval("atob(btoa('The quick brown fox'));"));
    }
}
