using System.Security.Cryptography;
using System.Text;
using Daisi.Broski.Docs;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Tests for PDF Standard Security decryption. Because building
/// an encrypted PDF inline is a non-trivial fixture, this test
/// uses the test-side <see cref="EncryptedMinimalPdf"/> helper
/// that replicates the spec's encryption algorithm forward — the
/// same MD5 cascade and AES-128-CBC the production decoder
/// reverses. A round-trip through both halves proves the algebra
/// is consistent and the converter sees the plaintext.
/// </summary>
public class PdfCryptoTests
{
    private const string PdfCt = "application/pdf";
    private static readonly Uri Url = new("https://example.com/locked.pdf");

    [Fact]
    public void V4_AES_128_empty_password_pdf_extracts_text()
    {
        var pdf = EncryptedMinimalPdf.BuildAesEncrypted(
            contentStream: "BT /F1 12 Tf (Encrypted body decoded successfully) Tj ET");
        bool ok = DocDispatcher.TryConvert(pdf, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        Assert.Contains("Encrypted body decoded successfully", html!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void V4_AES_128_unicode_metadata_decrypts_in_info_dict()
    {
        // Verifies string decryption (separate from stream
        // decryption): the title in /Info is encrypted under the
        // info object's number, and the converter has to decrypt
        // it before surfacing it as the article title.
        var pdf = EncryptedMinimalPdf.BuildAesEncrypted(
            contentStream: "BT /F1 12 Tf (Body) Tj ET",
            infoTitle: "Encrypted Title Survives");
        bool ok = DocDispatcher.TryConvert(pdf, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.Contains("Encrypted Title Survives", html!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void V2_RC4_128_empty_password_pdf_extracts_text()
    {
        var pdf = EncryptedMinimalPdf.BuildRc4Encrypted(
            contentStream: "BT /F1 12 Tf (RC4 stream cipher round trip) Tj ET");
        bool ok = DocDispatcher.TryConvert(pdf, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.Contains("RC4 stream cipher round trip", html!,
            StringComparison.Ordinal);
    }
}
