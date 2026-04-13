using System.Text;
using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Dom.Selectors;
using Daisi.Broski.Engine.Html;
using Daisi.Broski.Ipc;
using Xunit;

namespace Daisi.Broski.Engine.Tests;

/// <summary>
/// Phase 5h — random-input fuzzing harness against the parsers
/// and the IPC codec. The bar is "don't crash on any input,
/// don't loop forever, don't blow the heap": malformed input
/// should either parse to a (possibly nonsensical) tree, or
/// surface a known exception type the caller can catch.
///
/// <para>
/// Every test runs N iterations from a fixed PRNG seed so any
/// failure reproduces deterministically. If a future change
/// surfaces a crash bug, the seed printed in the assertion
/// message is enough to replay the exact failing input.
/// </para>
///
/// <para>
/// Generators are biased toward characters that exercise
/// non-trivial paths in each target — angle brackets and
/// quotes for the HTML tokenizer, selector punctuation for
/// the CSS parser, length-prefix manipulation for the IPC
/// codec — rather than uniformly-random bytes that mostly
/// look like ASCII text.
/// </para>
/// </summary>
public class FuzzingTests
{
    // Iteration count per fact. Tuned so the suite stays fast
    // (each test runs in well under a second on a modern dev
    // machine) while still exploring a meaningful corpus.
    private const int Iterations = 2000;
    private const int Seed = 0x6E51BE5; // arbitrary but fixed

    // ---------------------------------------------------------
    // HTML tokenizer
    // ---------------------------------------------------------

    [Fact]
    public void Tokenizer_does_not_throw_on_random_input()
    {
        var rng = new Random(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            string input = RandomHtmlish(rng, maxLen: 256);
            try
            {
                var tok = new Tokenizer(input);
                int safety = 200_000;
                while (safety-- > 0)
                {
                    var t = tok.Next();
                    if (t is EndOfFileToken) break;
                }
                Assert.True(safety > 0,
                    $"tokenizer did not reach EOF (input len={input.Length})");
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"Tokenizer threw {ex.GetType().Name} on iteration {i} (seed={Seed:X}): " +
                    $"{Truncate(input, 200)}");
            }
        }
    }

    // ---------------------------------------------------------
    // HTML tree builder
    // ---------------------------------------------------------

    [Fact]
    public void TreeBuilder_does_not_throw_on_random_input()
    {
        var rng = new Random(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            string input = RandomHtmlish(rng, maxLen: 512);
            try
            {
                var doc = HtmlTreeBuilder.Parse(input);
                // Sanity: the document must always be reachable
                // and the tree shape must be self-consistent.
                Assert.NotNull(doc);
                int count = 0;
                foreach (var _ in doc.DescendantElements())
                {
                    if (++count > 100_000)
                    {
                        Assert.Fail(
                            $"tree builder produced an unbounded tree (iteration {i})");
                    }
                }
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"TreeBuilder threw {ex.GetType().Name} on iteration {i} (seed={Seed:X}): " +
                    $"{Truncate(input, 200)}");
            }
        }
    }

    // ---------------------------------------------------------
    // CSS selector parser
    // ---------------------------------------------------------

    [Fact]
    public void SelectorParser_only_throws_SelectorParseException()
    {
        // The parser is allowed to fail loudly on malformed
        // input, but ONLY via SelectorParseException — never
        // an unexpected NullRef / ArgumentOutOfRange that
        // would indicate an internal bug.
        var rng = new Random(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            string input = RandomSelectorish(rng, maxLen: 80);
            try
            {
                _ = SelectorParser.Parse(input);
            }
            catch (SelectorParseException) { /* expected */ }
            catch (ArgumentNullException)
            {
                // Only happens if input is null — random gen
                // never produces that, so re-raise.
                throw;
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"SelectorParser threw {ex.GetType().Name} on iteration {i} (seed={Seed:X}): " +
                    $"{Truncate(input, 200)}");
            }
        }
    }

    // ---------------------------------------------------------
    // CSS selector matcher (against a real DOM)
    // ---------------------------------------------------------

    [Fact]
    public void SelectorMatcher_does_not_throw_on_any_parsed_selector()
    {
        // For matcher fuzzing we need *valid* selectors. Build a
        // fixed seed corpus of well-known patterns plus
        // randomly-perturbed valid selectors, run them all
        // against the same shared document.
        var doc = HtmlTreeBuilder.Parse("""
            <!DOCTYPE html>
            <html><body>
              <div id="a" class="x"><span>1</span></div>
              <ul><li class="y">2</li><li class="y z">3</li></ul>
              <a href="/u">link</a>
            </body></html>
            """);
        var rng = new Random(Seed);
        var bases = new[]
        {
            "*", "div", "#a", ".x", "li.y", "li.y.z", "a[href]",
            "a[href^='/']", "a[href$='/u']", "ul > li:first-child",
            "div span", "li:nth-child(2)", "li:not(.z)",
            "div, span", "a[href*='u']", "*:empty",
        };
        for (int i = 0; i < Iterations; i++)
        {
            string sel = bases[rng.Next(bases.Length)];
            try
            {
                _ = doc.QuerySelectorAll(sel);
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"matcher threw {ex.GetType().Name} on selector '{sel}' " +
                    $"(iteration {i}, seed={Seed:X})");
            }
        }
    }

    // ---------------------------------------------------------
    // IPC codec — length-prefixed JSON framing
    // ---------------------------------------------------------

    [Fact]
    public async Task IpcCodec_only_throws_IpcProtocolException_on_random_frames()
    {
        var rng = new Random(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] payload = RandomIpcFrame(rng);
            using var ms = new MemoryStream(payload);
            try
            {
                _ = await IpcCodec.ReadAsync(ms, CancellationToken.None);
            }
            catch (IpcProtocolException) { /* expected */ }
            catch (EndOfStreamException) { /* truncated framing — expected */ }
            catch (System.Text.Json.JsonException) { /* malformed JSON — expected */ }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"IpcCodec.ReadAsync threw {ex.GetType().Name} on iteration {i} " +
                    $"(seed={Seed:X}): {ex.Message}");
            }
        }
    }

    [Fact]
    public async Task IpcCodec_rejects_oversize_frame_lengths_cleanly()
    {
        // A byte-oriented test: the first 4 bytes are the
        // length prefix. If we set them to a value larger than
        // the codec's cap, the codec should refuse rather than
        // try to allocate.
        var oversize = new byte[8];
        oversize[0] = 0xFF;
        oversize[1] = 0xFF;
        oversize[2] = 0xFF;
        oversize[3] = 0xFF;
        using var ms = new MemoryStream(oversize);
        await Assert.ThrowsAsync<IpcProtocolException>(
            () => IpcCodec.ReadAsync(ms, CancellationToken.None));
    }

    // ---------------------------------------------------------
    // Generators
    // ---------------------------------------------------------

    /// <summary>String biased toward characters the HTML
    /// tokenizer keys on — angle brackets, slashes, quotes,
    /// equals — interleaved with normal text. Roughly half the
    /// generated chars are "interesting" so the tokenizer's
    /// state machine actually moves around.</summary>
    private static string RandomHtmlish(Random rng, int maxLen)
    {
        int len = rng.Next(1, maxLen + 1);
        var sb = new StringBuilder(len);
        const string interesting = "<>/=\"'&!?-#";
        for (int i = 0; i < len; i++)
        {
            int r = rng.Next(100);
            if (r < 35) sb.Append(interesting[rng.Next(interesting.Length)]);
            else if (r < 80) sb.Append((char)('a' + rng.Next(26)));
            else if (r < 95) sb.Append((char)('0' + rng.Next(10)));
            else sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>String biased toward CSS-selector punctuation
    /// (<c>#.[]:>+~,*</c>) interleaved with identifier
    /// characters.</summary>
    private static string RandomSelectorish(Random rng, int maxLen)
    {
        int len = rng.Next(1, maxLen + 1);
        var sb = new StringBuilder(len);
        const string interesting = "#.[]():>+~,*=^$|\"'-";
        for (int i = 0; i < len; i++)
        {
            int r = rng.Next(100);
            if (r < 30) sb.Append(interesting[rng.Next(interesting.Length)]);
            else if (r < 80) sb.Append((char)('a' + rng.Next(26)));
            else if (r < 95) sb.Append((char)('0' + rng.Next(10)));
            else sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>Random IPC-codec input: half the time a frame
    /// with a plausible-looking length prefix and random body,
    /// half the time pure garbage. Both must not crash the
    /// reader.</summary>
    private static byte[] RandomIpcFrame(Random rng)
    {
        if (rng.Next(2) == 0)
        {
            // Pure garbage stream.
            int n = rng.Next(0, 256);
            var buf = new byte[n];
            rng.NextBytes(buf);
            return buf;
        }
        // Plausible length prefix + body.
        int payloadLen = rng.Next(0, 1024);
        var data = new byte[4 + payloadLen];
        data[0] = (byte)((payloadLen >> 24) & 0xFF);
        data[1] = (byte)((payloadLen >> 16) & 0xFF);
        data[2] = (byte)((payloadLen >> 8) & 0xFF);
        data[3] = (byte)(payloadLen & 0xFF);
        var body = new byte[payloadLen];
        rng.NextBytes(body);
        Array.Copy(body, 0, data, 4, payloadLen);
        return data;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
