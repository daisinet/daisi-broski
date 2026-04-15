using System.Text;
using Daisi.Broski.Docs;
using Daisi.Broski.Skimmer;
using Xunit;
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// End-to-end tests for PDF image extraction. Builds a minimal
/// PDF inline with an embedded JPEG as an image XObject, runs it
/// through the converter + Skimmer, and checks that the image
/// flows through to <c>ArticleContent.HeroImage</c> and
/// <c>ArticleContent.Images</c> via the synthetic HTML path.
/// </summary>
public class PdfImageTests
{
    private const string PdfCt = "application/pdf";
    private static readonly Uri Url = new("https://example.com/illustrated.pdf");

    // 107-byte synthetic JPEG that parses cleanly. Embedded as a
    // base64 constant so the test has no dependency on an image
    // encoder; the byte sequence is a minimum-viable JFIF that
    // PDF readers accept.
    private const string TinyJpegBase64 =
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIf" +
        "IiEmKzcvJik0KSEiMEExNDk7Pj4+JS5ESUM8SDc9Pjv/2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7" +
        "Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozv/wAARCAABAAEDASIA" +
        "AhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAn/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFAEB" +
        "AAAAAAAAAAAAAAAAAAAAAP/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/ACd//9k=";

    [Fact]
    public void Pdf_with_jpeg_xobject_exposes_image_and_hero()
    {
        var pdf = BuildPdfWithJpeg();
        bool ok = DocDispatcher.TryConvert(pdf, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        // The synthetic HTML should carry both a hero-image meta
        // and at least one inline <img> with a data:image/jpeg URI.
        Assert.Contains("og:image", html!, StringComparison.Ordinal);
        Assert.Contains("<img src=\"data:image/jpeg;base64,",
            html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skim_of_pdf_with_jpeg_populates_HeroImage_and_Images()
    {
        var pdf = BuildPdfWithJpeg();
        var article = await SkimmerApi.SkimAsync(
            new Uri("https://example.com/x.pdf"),
            new SkimmerOptions
            {
                ScriptingEnabled = false,
                Fetcher = new Engine.Net.HttpFetcherOptions
                {
                    Interceptor = _ => new Engine.Net.InterceptedResponse
                    {
                        Status = 200,
                        ContentType = PdfCt,
                        Body = pdf,
                    },
                },
            });
        Assert.NotNull(article.HeroImage);
        Assert.StartsWith("data:image/jpeg;base64,", article.HeroImage!,
            StringComparison.Ordinal);
        Assert.NotEmpty(article.Images);
    }

    // ---------- fixture ----------

    private static byte[] BuildPdfWithJpeg()
    {
        byte[] jpeg = System.Convert.FromBase64String(TinyJpegBase64);
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WRaw(byte[] b) => ms.Write(b);

        W("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        long catalogOff = ms.Length;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long pagesOff = ms.Length;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        long pageOff = ms.Length;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
          "/Resources << /Font << /F1 4 0 R >> /XObject << /Im1 5 0 R >> >> " +
          "/Contents 6 0 R >>\nendobj\n");

        long fontOff = ms.Length;
        W("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        long imageOff = ms.Length;
        W("5 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 " +
          $"/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        WRaw(jpeg);
        W("\nendstream\nendobj\n");

        long contentsOff = ms.Length;
        var cs = Encoding.ASCII.GetBytes("BT /F1 12 Tf (Caption under the image) Tj ET");
        W($"6 0 obj\n<< /Length {cs.Length} >>\nstream\n");
        WRaw(cs);
        W("\nendstream\nendobj\n");

        long xrefOff = ms.Length;
        W("xref\n0 7\n");
        W("0000000000 65535 f \n");
        W($"{catalogOff:D10} 00000 n \n");
        W($"{pagesOff:D10} 00000 n \n");
        W($"{pageOff:D10} 00000 n \n");
        W($"{fontOff:D10} 00000 n \n");
        W($"{imageOff:D10} 00000 n \n");
        W($"{contentsOff:D10} 00000 n \n");
        W("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOff}\n%%EOF\n");
        return ms.ToArray();
    }
}
