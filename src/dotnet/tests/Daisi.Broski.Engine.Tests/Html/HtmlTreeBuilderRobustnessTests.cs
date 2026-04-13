using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Html;

/// <summary>
/// Robustness tests for the HTML tree builder against malformed
/// real-world input. Without these guards, sites that emit
/// stray text after </body> (LinkedIn /in/* profile pages,
/// among others) crashed the host with an
/// InvalidOperationException.
/// </summary>
public class HtmlTreeBuilderRobustnessTests
{
    [Fact]
    public void Text_after_explicit_body_close_does_not_crash()
    {
        // Malformed input pattern observed on linkedin.com/in/* —
        // characters appear in the token stream after the open-
        // elements stack has been drained by an early body close.
        // The parser used to throw InvalidOperationException
        // ("No open elements") and crash the host; the fix is a
        // graceful no-op fallback in InsertText.
        var html = "<html><head></head><body>before</body></html>after";
        var doc = HtmlTreeBuilder.Parse(html);
        Assert.NotNull(doc);
        Assert.NotNull(doc.Body);
        Assert.Contains("before", doc.Body!.TextContent);
    }
}
