using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Dom;

/// <summary>
/// Phase 6.x — <see cref="UrlResolver"/>. Regression guard
/// for the Blazor-breaker bug where relative script srcs
/// on pages with <c>&lt;base href="/"&gt;</c> were resolved
/// against the current path's directory (producing
/// <c>/account/_framework/blazor.web.js</c>) instead of the
/// document base (<c>/_framework/blazor.web.js</c>). The
/// server returned its SPA shell HTML for the wrong URL,
/// the JS parser choked on <c>&lt;!DOCTYPE html&gt;</c>,
/// and nothing Blazor-related bootstrapped.
/// </summary>
public class UrlResolverTests
{
    [Fact]
    public void Resolves_relative_against_page_when_no_base_href()
    {
        var doc = HtmlTreeBuilder.Parse(
            "<html><head></head><body></body></html>");
        var pageUrl = new Uri("https://example.com/one/two/page.html");
        var r = UrlResolver.Resolve(doc, pageUrl, "img.png");
        Assert.NotNull(r);
        Assert.Equal("https://example.com/one/two/img.png", r!.AbsoluteUri);
    }

    [Fact]
    public void Base_href_root_rewinds_to_site_root()
    {
        // This is the Blazor case — <base href="/"> means
        // every relative URL pivots on the origin root,
        // not the current path.
        var doc = HtmlTreeBuilder.Parse(
            """<html><head><base href="/"></head><body></body></html>""");
        var pageUrl = new Uri("https://manager.daisinet.com/account/register");
        var r = UrlResolver.Resolve(doc, pageUrl, "_framework/blazor.web.b9228eflpl.js");
        Assert.NotNull(r);
        Assert.Equal(
            "https://manager.daisinet.com/_framework/blazor.web.b9228eflpl.js",
            r!.AbsoluteUri);
    }

    [Fact]
    public void Absolute_base_href_pins_to_external_origin()
    {
        var doc = HtmlTreeBuilder.Parse(
            """<html><head><base href="https://cdn.example.com/v2/"></head><body></body></html>""");
        var pageUrl = new Uri("https://site.example.com/page");
        var r = UrlResolver.Resolve(doc, pageUrl, "lib.js");
        Assert.NotNull(r);
        Assert.Equal("https://cdn.example.com/v2/lib.js", r!.AbsoluteUri);
    }

    [Fact]
    public void Relative_base_href_shifts_by_subdir()
    {
        var doc = HtmlTreeBuilder.Parse(
            """<html><head><base href="sub/"></head><body></body></html>""");
        var pageUrl = new Uri("https://example.com/one/two/page.html");
        var r = UrlResolver.Resolve(doc, pageUrl, "img.png");
        Assert.NotNull(r);
        // Resolution: base-href "sub/" resolves against
        // pageUrl → /one/two/sub/, then "img.png" resolves
        // against that → /one/two/sub/img.png
        Assert.Equal("https://example.com/one/two/sub/img.png", r!.AbsoluteUri);
    }

    [Fact]
    public void Absolute_href_ignores_base()
    {
        // An already-absolute href doesn't get rewritten by
        // base — standard URL resolution behavior.
        var doc = HtmlTreeBuilder.Parse(
            """<html><head><base href="/"></head><body></body></html>""");
        var pageUrl = new Uri("https://example.com/x");
        var r = UrlResolver.Resolve(doc, pageUrl, "https://other.example/abs.js");
        Assert.Equal("https://other.example/abs.js", r!.AbsoluteUri);
    }

    [Fact]
    public void Null_document_falls_back_to_page_url()
    {
        var pageUrl = new Uri("https://example.com/one/page");
        var r = UrlResolver.Resolve(null, pageUrl, "img.png");
        Assert.NotNull(r);
        Assert.Equal("https://example.com/one/img.png", r!.AbsoluteUri);
    }

    [Fact]
    public void Empty_href_returns_null()
    {
        var doc = HtmlTreeBuilder.Parse("<html></html>");
        var pageUrl = new Uri("https://example.com/");
        Assert.Null(UrlResolver.Resolve(doc, pageUrl, ""));
        Assert.Null(UrlResolver.Resolve(doc, pageUrl, null));
    }

    [Fact]
    public void Only_base_in_head_is_considered()
    {
        // A rogue <base> outside <head> is ignored per spec
        // — some pages inject them dynamically into <body>
        // and they shouldn't affect URL resolution for
        // statically-parsed script tags.
        var doc = HtmlTreeBuilder.Parse(
            """<html><head></head><body><base href="/ignored/"></body></html>""");
        var pageUrl = new Uri("https://example.com/a/b/page");
        var r = UrlResolver.Resolve(doc, pageUrl, "img.png");
        Assert.Equal("https://example.com/a/b/img.png", r!.AbsoluteUri);
    }
}
