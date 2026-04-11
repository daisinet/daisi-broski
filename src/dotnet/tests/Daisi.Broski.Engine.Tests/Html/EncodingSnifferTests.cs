using System.Text;
using Daisi.Broski.Engine.Html;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Html;

public class EncodingSnifferTests
{
    [Fact]
    public void Utf8_BOM_is_detected()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'<', (byte)'h', (byte)'1', (byte)'>'];
        var enc = EncodingSniffer.Sniff(bytes);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Utf16_LE_BOM_is_detected()
    {
        byte[] bytes = [0xFF, 0xFE, (byte)'<', 0x00];
        var enc = EncodingSniffer.Sniff(bytes);
        Assert.Equal(Encoding.Unicode, enc);
    }

    [Fact]
    public void Utf16_BE_BOM_is_detected()
    {
        byte[] bytes = [0xFE, 0xFF, 0x00, (byte)'<'];
        var enc = EncodingSniffer.Sniff(bytes);
        Assert.Equal(Encoding.BigEndianUnicode, enc);
    }

    [Fact]
    public void BOM_beats_content_type_header()
    {
        // BOM is UTF-8 but header claims ASCII; BOM wins per spec.
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'x'];
        var enc = EncodingSniffer.Sniff(bytes, "text/html; charset=us-ascii");
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Content_type_charset_parameter_is_respected()
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><title>hi</title>");
        var enc = EncodingSniffer.Sniff(body, "text/html; charset=utf-8");
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Content_type_charset_is_respected_when_quoted()
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html>");
        var enc = EncodingSniffer.Sniff(body, "text/html; charset=\"utf-8\"");
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Content_type_with_surrounding_whitespace_still_parses()
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html>");
        var enc = EncodingSniffer.Sniff(body, "text/html ; charset = us-ascii");
        Assert.Equal(Encoding.ASCII, enc);
    }

    [Fact]
    public void Meta_charset_short_form_is_picked_up_by_prescan()
    {
        var body = Encoding.ASCII.GetBytes(
            "<!doctype html><meta charset=\"utf-8\"><title>hi</title>");
        var enc = EncodingSniffer.Sniff(body);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Meta_http_equiv_content_type_is_picked_up_by_prescan()
    {
        var body = Encoding.ASCII.GetBytes(
            "<!doctype html>" +
            "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=us-ascii\">");
        var enc = EncodingSniffer.Sniff(body);
        Assert.Equal(Encoding.ASCII, enc);
    }

    [Fact]
    public void Unquoted_meta_charset_is_picked_up()
    {
        var body = Encoding.ASCII.GetBytes("<!doctype html><meta charset=utf-8>");
        var enc = EncodingSniffer.Sniff(body);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Unknown_charset_name_falls_back_to_utf8()
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html>");
        var enc = EncodingSniffer.Sniff(body, "text/html; charset=nonsense-12345");
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Empty_input_falls_back_to_utf8()
    {
        var enc = EncodingSniffer.Sniff([]);
        Assert.Equal(Encoding.UTF8, enc);
    }

    [Fact]
    public void Meta_beyond_1024_bytes_is_ignored()
    {
        // Pad 1100 bytes of ASCII, then a meta charset tag. Prescan
        // window is 1024 bytes, so the tag shouldn't be found.
        var sb = new StringBuilder();
        sb.Append(' ', 1100);
        sb.Append("<meta charset=\"us-ascii\">");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());

        var enc = EncodingSniffer.Sniff(bytes);
        Assert.Equal(Encoding.UTF8, enc); // fallback, not ASCII
    }

    [Fact]
    public void Content_type_beats_meta_prescan_when_both_present()
    {
        // Header says ASCII, body's meta tag says UTF-8. Header wins
        // because Content-Type is checked before the prescan.
        var body = Encoding.ASCII.GetBytes("<!doctype html><meta charset=\"utf-8\">");
        var enc = EncodingSniffer.Sniff(body, "text/html; charset=us-ascii");
        Assert.Equal(Encoding.ASCII, enc);
    }
}
