using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-5: Web Crypto API subset — <c>crypto.getRandomValues</c>
/// and <c>crypto.randomUUID</c>. Both delegate to .NET's OS-backed
/// CSPRNG so the tests check contract (mutates in place, returns the
/// same buffer, throws on misuse, UUIDs are properly formed) rather
/// than bit-level randomness.
/// </summary>
public class JsCryptoTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    [Fact]
    public void Crypto_global_exists()
    {
        Assert.Equal("object", Eval("typeof crypto;"));
    }

    [Fact]
    public void GetRandomValues_fills_uint8array()
    {
        // A freshly-allocated Uint8Array is all zeros; after
        // getRandomValues at least one byte should almost
        // certainly be non-zero. "At least one non-zero" has
        // a false-negative probability of 1/256^16 ≈ 2e-39.
        Assert.Equal(
            true,
            Eval(@"
                var buf = new Uint8Array(16);
                crypto.getRandomValues(buf);
                var anyNonZero = false;
                for (var i = 0; i < buf.length; i++) {
                    if (buf[i] !== 0) { anyNonZero = true; break; }
                }
                anyNonZero;
            "));
    }

    [Fact]
    public void GetRandomValues_returns_same_buffer()
    {
        Assert.Equal(
            true,
            Eval(@"
                var buf = new Uint8Array(8);
                crypto.getRandomValues(buf) === buf;
            "));
    }

    [Fact]
    public void GetRandomValues_fills_int32array()
    {
        // Int32Array length = 4 means 16 bytes. At least one
        // should be non-zero.
        Assert.Equal(
            true,
            Eval(@"
                var buf = new Int32Array(4);
                crypto.getRandomValues(buf);
                var allZero = true;
                for (var i = 0; i < buf.length; i++) {
                    if (buf[i] !== 0) { allZero = false; break; }
                }
                !allZero;
            "));
    }

    [Fact]
    public void GetRandomValues_rejects_float_arrays()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { crypto.getRandomValues(new Float32Array(4)); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void GetRandomValues_rejects_quota_over_64k()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { crypto.getRandomValues(new Uint8Array(65537)); false; }
                catch (e) { e instanceof RangeError; }
            "));
    }

    [Fact]
    public void GetRandomValues_rejects_non_typed_array()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { crypto.getRandomValues([1, 2, 3]); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void RandomUUID_returns_36_char_string()
    {
        Assert.Equal(36.0, Eval("crypto.randomUUID().length;"));
    }

    [Fact]
    public void RandomUUID_has_version_4_marker()
    {
        // Per RFC 4122: positions 14 is '4' (version) and 19
        // is one of '8', '9', 'a', 'b' (variant 10xx).
        Assert.Equal(
            true,
            Eval(@"
                var u = crypto.randomUUID();
                var versionChar = u.charAt(14);
                var variantChar = u.charAt(19);
                versionChar === '4' &&
                (variantChar === '8' || variantChar === '9' ||
                 variantChar === 'a' || variantChar === 'b');
            "));
    }

    [Fact]
    public void RandomUUID_returns_different_values()
    {
        Assert.Equal(
            true,
            Eval("crypto.randomUUID() !== crypto.randomUUID();"));
    }

    [Fact]
    public void RandomUUID_is_lowercase()
    {
        Assert.Equal(
            true,
            Eval(@"
                var u = crypto.randomUUID();
                u === u.toLowerCase();
            "));
    }
}
