using Daisi.Broski.Docs;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs;

/// <summary>
/// Pins the escape rules used by every doc → HTML converter. Converters
/// pass user-controlled text (run text, cell values, PDF text strings)
/// through here before writing into a StringBuilder, so any miss in
/// <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, or attribute-value quoting
/// is a cross-site-scripting vector once the synthetic HTML is handed
/// to <c>HtmlTreeBuilder.Parse</c>.
/// </summary>
public class HtmlWriterTests
{
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("<script>", "&lt;script&gt;")]
    [InlineData("\"quoted\"", "&quot;quoted&quot;")]
    [InlineData("it's fine", "it&#39;s fine")]
    [InlineData("", "")]
    public void EscapeText_handles_html_metacharacters(string input, string expected)
    {
        Assert.Equal(expected, HtmlWriter.EscapeText(input));
    }

    [Theory]
    [InlineData("normal", "normal")]
    [InlineData("quote\"in", "quote&quot;in")]
    [InlineData("amp&amp", "amp&amp;amp")]
    public void EscapeAttr_quotes_double_quotes(string input, string expected)
    {
        Assert.Equal(expected, HtmlWriter.EscapeAttr(input));
    }

    [Fact]
    public void EscapeText_null_returns_empty()
    {
        Assert.Equal("", HtmlWriter.EscapeText(null));
    }

    [Fact]
    public void EscapeAttr_null_returns_empty()
    {
        Assert.Equal("", HtmlWriter.EscapeAttr(null));
    }
}
