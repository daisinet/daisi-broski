using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Dom.Selectors;
using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Dom;

public class SelectorTests
{
    // -------- type, id, class --------

    [Fact]
    public void Type_selector_matches_tag_name()
    {
        var doc = HtmlTreeBuilder.Parse("<div><p>x</p><a>y</a></div>");
        var p = doc.QuerySelector("p");
        Assert.NotNull(p);
        Assert.Equal("p", p!.TagName);
    }

    [Fact]
    public void Universal_selector_matches_any_element()
    {
        var doc = HtmlTreeBuilder.Parse("<div><p>x</p></div>");
        // First element in the body (body itself is excluded from QuerySelector result
        // because the search is strictly descendant — it starts walking from this doc).
        var first = doc.Body!.QuerySelector("*");
        Assert.NotNull(first);
        Assert.Equal("div", first!.TagName);
    }

    [Fact]
    public void Id_selector_matches_element_by_id()
    {
        var doc = HtmlTreeBuilder.Parse("<p id=\"intro\">hi</p><p>two</p>");
        var p = doc.QuerySelector("#intro");
        Assert.NotNull(p);
        Assert.Equal("intro", p!.Id);
    }

    [Fact]
    public void Class_selector_matches_any_class_token()
    {
        var doc = HtmlTreeBuilder.Parse("<a class=\"primary button\">x</a><a class=\"button\">y</a>");
        var buttons = doc.QuerySelectorAll(".button");
        Assert.Equal(2, buttons.Count);

        var primary = doc.QuerySelectorAll(".primary");
        Assert.Single(primary);
    }

    // -------- compound --------

    [Fact]
    public void Compound_selector_requires_all_constraints()
    {
        var doc = HtmlTreeBuilder.Parse("<a class=\"x\">1</a><a class=\"x y\">2</a><div class=\"x\">3</div>");
        var result = doc.QuerySelectorAll("a.x");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Compound_with_id_and_class()
    {
        var doc = HtmlTreeBuilder.Parse("<div id=\"main\" class=\"container\">x</div>");
        Assert.NotNull(doc.QuerySelector("div#main.container"));
    }

    // -------- attribute selectors --------

    [Fact]
    public void Attribute_exists()
    {
        var doc = HtmlTreeBuilder.Parse("<a href=\"/x\">1</a><a>2</a>");
        var result = doc.QuerySelectorAll("a[href]");
        Assert.Single(result);
    }

    [Fact]
    public void Attribute_equals()
    {
        var doc = HtmlTreeBuilder.Parse("<a href=\"/x\">1</a><a href=\"/y\">2</a>");
        var result = doc.QuerySelectorAll("a[href=\"/y\"]");
        Assert.Single(result);
        Assert.Equal("2", result[0].TextContent);
    }

    [Fact]
    public void Attribute_starts_with()
    {
        var doc = HtmlTreeBuilder.Parse("<a href=\"/a\">1</a><a href=\"/b\">2</a><a href=\"x\">3</a>");
        var result = doc.QuerySelectorAll("a[href^=\"/\"]");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Attribute_ends_with()
    {
        var doc = HtmlTreeBuilder.Parse("<a href=\"x.png\">1</a><a href=\"y.jpg\">2</a>");
        var result = doc.QuerySelectorAll("a[href$=\".png\"]");
        Assert.Single(result);
    }

    [Fact]
    public void Attribute_substring()
    {
        var doc = HtmlTreeBuilder.Parse("<a href=\"https://example.com/\">1</a><a href=\"/local\">2</a>");
        var result = doc.QuerySelectorAll("a[href*=\"example\"]");
        Assert.Single(result);
    }

    [Fact]
    public void Attribute_contains_word()
    {
        var doc = HtmlTreeBuilder.Parse("<p class=\"a b c\">1</p><p class=\"ab\">2</p>");
        var result = doc.QuerySelectorAll("[class~=\"b\"]");
        Assert.Single(result);
        Assert.Equal("1", result[0].TextContent);
    }

    [Fact]
    public void Attribute_dash_prefix()
    {
        var doc = HtmlTreeBuilder.Parse("<p lang=\"en\">1</p><p lang=\"en-GB\">2</p><p lang=\"fr\">3</p>");
        var result = doc.QuerySelectorAll("p[lang|=\"en\"]");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Attribute_case_insensitive_flag()
    {
        var doc = HtmlTreeBuilder.Parse("<a href=\"/HELLO\">1</a>");
        Assert.NotNull(doc.QuerySelector("a[href=\"/hello\" i]"));
    }

    // -------- combinators --------

    [Fact]
    public void Descendant_combinator()
    {
        var doc = HtmlTreeBuilder.Parse("<div><span><a>inner</a></span></div><a>outer</a>");
        var result = doc.QuerySelectorAll("div a");
        Assert.Single(result);
        Assert.Equal("inner", result[0].TextContent);
    }

    [Fact]
    public void Child_combinator()
    {
        var doc = HtmlTreeBuilder.Parse("<div><a>direct</a><span><a>indirect</a></span></div>");
        var result = doc.QuerySelectorAll("div > a");
        Assert.Single(result);
        Assert.Equal("direct", result[0].TextContent);
    }

    [Fact]
    public void Adjacent_sibling_combinator()
    {
        var doc = HtmlTreeBuilder.Parse("<h1>a</h1><p>yes</p><p>no</p>");
        var result = doc.QuerySelectorAll("h1 + p");
        Assert.Single(result);
        Assert.Equal("yes", result[0].TextContent);
    }

    [Fact]
    public void General_sibling_combinator()
    {
        var doc = HtmlTreeBuilder.Parse("<h1>a</h1><p>one</p><div>x</div><p>two</p>");
        var result = doc.QuerySelectorAll("h1 ~ p");
        Assert.Equal(2, result.Count);
    }

    // -------- selector lists --------

    [Fact]
    public void Selector_list_matches_any_branch()
    {
        var doc = HtmlTreeBuilder.Parse("<div>1</div><span>2</span><a>3</a>");
        var result = doc.QuerySelectorAll("div, a");
        Assert.Equal(2, result.Count);
    }

    // -------- pseudo-classes --------

    [Fact]
    public void First_child_and_last_child()
    {
        var doc = HtmlTreeBuilder.Parse("<ul><li>a</li><li>b</li><li>c</li></ul>");
        var first = doc.QuerySelector("li:first-child");
        var last = doc.QuerySelector("li:last-child");
        Assert.Equal("a", first!.TextContent);
        Assert.Equal("c", last!.TextContent);
    }

    [Fact]
    public void Nth_child_numeric()
    {
        var doc = HtmlTreeBuilder.Parse("<ul><li>a</li><li>b</li><li>c</li><li>d</li></ul>");
        var second = doc.QuerySelector("li:nth-child(2)");
        Assert.Equal("b", second!.TextContent);
    }

    [Fact]
    public void Nth_child_expression_2n_plus_1()
    {
        // :nth-child(2n+1) → odd positions 1, 3, 5...
        var doc = HtmlTreeBuilder.Parse("<ul><li>a</li><li>b</li><li>c</li><li>d</li></ul>");
        var odd = doc.QuerySelectorAll("li:nth-child(2n+1)");
        Assert.Equal(2, odd.Count);
        Assert.Equal("a", odd[0].TextContent);
        Assert.Equal("c", odd[1].TextContent);
    }

    [Fact]
    public void Nth_child_odd_and_even_keywords()
    {
        var doc = HtmlTreeBuilder.Parse("<ul><li>1</li><li>2</li><li>3</li><li>4</li></ul>");
        var odd = doc.QuerySelectorAll("li:nth-child(odd)");
        var even = doc.QuerySelectorAll("li:nth-child(even)");
        Assert.Equal(2, odd.Count);
        Assert.Equal(2, even.Count);
        Assert.Equal("1", odd[0].TextContent);
        Assert.Equal("2", even[0].TextContent);
    }

    [Fact]
    public void First_of_type_vs_first_child()
    {
        // The first <p> is not the first child (h1 is), but it is the
        // first of its type.
        var doc = HtmlTreeBuilder.Parse("<div><h1>a</h1><p>b</p><p>c</p></div>");
        var firstP = doc.QuerySelector("p:first-of-type");
        Assert.Equal("b", firstP!.TextContent);

        // :first-child p matches nothing because the first child is h1.
        Assert.Null(doc.QuerySelector("p:first-child"));
    }

    [Fact]
    public void Not_pseudo_class_excludes_matching_elements()
    {
        var doc = HtmlTreeBuilder.Parse("<div><a class=\"x\">1</a><a>2</a><a class=\"x\">3</a></div>");
        var result = doc.QuerySelectorAll("a:not(.x)");
        Assert.Single(result);
        Assert.Equal("2", result[0].TextContent);
    }

    [Fact]
    public void Empty_pseudo_class_matches_elements_with_no_content()
    {
        var doc = HtmlTreeBuilder.Parse("<div><p></p><p>x</p><p><span></span></p></div>");
        var empty = doc.QuerySelectorAll("p:empty");
        Assert.Single(empty);
    }

    // -------- Matches / Closest --------

    [Fact]
    public void Matches_returns_true_when_selector_matches_element()
    {
        var doc = HtmlTreeBuilder.Parse("<a class=\"primary\" href=\"/x\">hi</a>");
        var a = doc.QuerySelector("a")!;
        Assert.True(a.Matches("a.primary[href^=\"/\"]"));
        Assert.False(a.Matches("a.secondary"));
    }

    [Fact]
    public void Closest_walks_ancestors_returning_innermost_match()
    {
        var doc = HtmlTreeBuilder.Parse("<section><article><p id=\"target\">x</p></article></section>");
        var p = doc.GetElementById("target")!;
        Assert.Equal("article", p.Closest("article")!.TagName);
        Assert.Equal("section", p.Closest("section")!.TagName);
        Assert.Null(p.Closest("main"));
    }

    [Fact]
    public void Closest_returns_self_when_self_matches()
    {
        var doc = HtmlTreeBuilder.Parse("<div class=\"x\"><p>hi</p></div>");
        var div = doc.QuerySelector("div")!;
        Assert.Same(div, div.Closest(".x"));
    }

    // -------- parser failure modes --------

    [Fact]
    public void Parser_rejects_pseudo_elements()
    {
        Assert.Throws<SelectorParseException>(() => SelectorParser.Parse("p::before"));
    }

    [Fact]
    public void Parser_rejects_unsupported_pseudo_class()
    {
        Assert.Throws<SelectorParseException>(() => SelectorParser.Parse(":hover"));
    }

    // -------- realistic demo --------

    [Fact]
    public void Phase_1_ship_gate_style_demo()
    {
        // Simulates the eventual HN test: select links with class="storylink".
        const string html = """
            <html><body>
              <table>
                <tr><td><a class="storylink" href="/1">First</a></td></tr>
                <tr><td><a class="storylink" href="/2">Second</a></td></tr>
                <tr><td><a href="/ad">Ad</a></td></tr>
                <tr><td><a class="storylink" href="/3">Third</a></td></tr>
              </table>
            </body></html>
            """;

        var doc = HtmlTreeBuilder.Parse(html);
        var stories = doc.QuerySelectorAll(".storylink");
        Assert.Equal(3, stories.Count);
        Assert.Equal(["First", "Second", "Third"], stories.Select(s => s.TextContent).ToArray());
    }
}
