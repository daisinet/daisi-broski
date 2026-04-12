using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Host API shims — <c>localStorage</c>,
/// <c>sessionStorage</c>, <c>navigator</c>,
/// <c>location</c>, <c>performance</c>, and the window
/// viewport surface. Each is a thin facade; tests
/// verify the shape + round-trip semantics rather than
/// integrated behavior.
/// </summary>
public class JsBrowserHostTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // localStorage / sessionStorage
    // ========================================================

    [Fact]
    public void LocalStorage_exists()
    {
        Assert.Equal("object", Eval("typeof localStorage;"));
    }

    [Fact]
    public void LocalStorage_set_and_get_round_trip()
    {
        Assert.Equal(
            "bar",
            Eval(@"
                localStorage.setItem('foo', 'bar');
                localStorage.getItem('foo');
            "));
    }

    [Fact]
    public void LocalStorage_missing_key_returns_null()
    {
        Assert.Equal(
            JsValue.Null,
            Eval("localStorage.getItem('nope');"));
    }

    [Fact]
    public void LocalStorage_length_reflects_item_count()
    {
        Assert.Equal(
            2.0,
            Eval(@"
                localStorage.setItem('a', '1');
                localStorage.setItem('b', '2');
                localStorage.length;
            "));
    }

    [Fact]
    public void LocalStorage_removeItem()
    {
        Assert.Equal(
            JsValue.Null,
            Eval(@"
                localStorage.setItem('k', 'v');
                localStorage.removeItem('k');
                localStorage.getItem('k');
            "));
    }

    [Fact]
    public void LocalStorage_clear_empties_storage()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                localStorage.setItem('a', '1');
                localStorage.setItem('b', '2');
                localStorage.clear();
                localStorage.length;
            "));
    }

    [Fact]
    public void LocalStorage_key_by_index()
    {
        Assert.Equal(
            "a",
            Eval(@"
                localStorage.setItem('a', '1');
                localStorage.setItem('b', '2');
                localStorage.key(0);
            "));
    }

    [Fact]
    public void SessionStorage_is_independent_from_localStorage()
    {
        Assert.Equal(
            JsValue.Null,
            Eval(@"
                localStorage.setItem('shared', 'local-only');
                sessionStorage.getItem('shared');
            "));
    }

    // ========================================================
    // navigator
    // ========================================================

    [Fact]
    public void Navigator_userAgent_is_string()
    {
        Assert.Equal("string", Eval("typeof navigator.userAgent;"));
    }

    [Fact]
    public void Navigator_userAgent_contains_daisi()
    {
        Assert.Equal(
            true,
            Eval("navigator.userAgent.indexOf('daisi-broski') >= 0;"));
    }

    [Fact]
    public void Navigator_language_is_en_US()
    {
        Assert.Equal("en-US", Eval("navigator.language;"));
    }

    [Fact]
    public void Navigator_languages_is_array()
    {
        Assert.Equal(
            2.0,
            Eval("navigator.languages.length;"));
    }

    [Fact]
    public void Navigator_onLine_is_true()
    {
        Assert.Equal(true, Eval("navigator.onLine;"));
    }

    [Fact]
    public void Navigator_hardwareConcurrency_is_positive()
    {
        Assert.Equal(
            true,
            Eval("navigator.hardwareConcurrency > 0;"));
    }

    // ========================================================
    // location
    // ========================================================

    [Fact]
    public void Location_exists()
    {
        Assert.Equal("object", Eval("typeof location;"));
    }

    [Fact]
    public void Location_default_href_is_about_blank()
    {
        Assert.Equal("about:blank", Eval("location.href;"));
    }

    [Fact]
    public void Location_href_can_be_overridden_from_script()
    {
        Assert.Equal(
            "https://example.com/x",
            Eval(@"
                location.href = 'https://example.com/x';
                location.href;
            "));
    }

    [Fact]
    public void Location_components_split_from_overridden_href()
    {
        Assert.Equal(
            "example.com|/x|?q=1|#frag",
            Eval(@"
                location.href = 'https://example.com/x?q=1#frag';
                [location.hostname, location.pathname, location.search, location.hash].join('|');
            "));
    }

    // ========================================================
    // performance
    // ========================================================

    [Fact]
    public void Performance_now_returns_number()
    {
        Assert.Equal("number", Eval("typeof performance.now();"));
    }

    [Fact]
    public void Performance_now_monotonic()
    {
        Assert.Equal(
            true,
            Eval(@"
                var a = performance.now();
                var b = performance.now();
                b >= a;
            "));
    }

    [Fact]
    public void Performance_timeOrigin_is_number()
    {
        Assert.Equal("number", Eval("typeof performance.timeOrigin;"));
    }

    [Fact]
    public void Performance_mark_is_no_op()
    {
        // Should not throw; scripts that call mark/measure
        // as fire-and-forget should work.
        Assert.Equal(
            "undefined",
            Eval("typeof performance.mark('begin');"));
    }

    // ========================================================
    // window viewport
    // ========================================================

    [Fact]
    public void Window_innerWidth_is_1280()
    {
        Assert.Equal(1280.0, Eval("window.innerWidth;"));
    }

    [Fact]
    public void Window_innerHeight_is_800()
    {
        Assert.Equal(800.0, Eval("window.innerHeight;"));
    }

    [Fact]
    public void Window_devicePixelRatio_is_one()
    {
        Assert.Equal(1.0, Eval("window.devicePixelRatio;"));
    }

    [Fact]
    public void Window_scrollTo_is_no_op()
    {
        Assert.Equal(
            "undefined",
            Eval("typeof window.scrollTo(0, 100);"));
    }

    [Fact]
    public void MatchMedia_returns_MediaQueryList_shape()
    {
        Assert.Equal(
            false,
            Eval("matchMedia('(prefers-color-scheme: dark)').matches;"));
    }

    [Fact]
    public void MatchMedia_preserves_query()
    {
        Assert.Equal(
            "(min-width: 800px)",
            Eval("matchMedia('(min-width: 800px)').media;"));
    }

    [Fact]
    public void MatchMedia_addListener_is_no_op()
    {
        Assert.Equal(
            "undefined",
            Eval("typeof matchMedia('(prefers-reduced-motion: reduce)').addListener(function () {});"));
    }
}
