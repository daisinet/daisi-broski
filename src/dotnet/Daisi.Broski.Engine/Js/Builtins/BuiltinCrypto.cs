using System.Security.Cryptography;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>crypto.getRandomValues</c> and <c>crypto.randomUUID</c>
/// — the two Web Crypto API methods that real-world script
/// actually reaches for. Both delegate to .NET's
/// <see cref="System.Security.Cryptography.RandomNumberGenerator"/>,
/// which wraps the OS CSPRNG (CryptGenRandom on Windows,
/// /dev/urandom on POSIX).
///
/// <para>
/// Deferred: the rest of <c>SubtleCrypto</c> — digest,
/// sign / verify, encrypt / decrypt, key generation, import /
/// export. Those land as their own slice because each
/// algorithm is a substantial spec-by-spec implementation,
/// and <c>getRandomValues</c> + <c>randomUUID</c> are the
/// two primitives most app code needs — session IDs,
/// nonces, dedup keys, request IDs.
/// </para>
/// </summary>
internal static class BuiltinCrypto
{
    public static void Install(JsEngine engine)
    {
        var crypto = new JsObject { Prototype = engine.ObjectPrototype };

        crypto.SetNonEnumerable("getRandomValues", new JsFunction("getRandomValues", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsTypedArray target)
            {
                JsThrow.TypeError("crypto.getRandomValues: argument must be an integer TypedArray");
                return null;
            }
            // Spec: throws QuotaExceededError (mapped here to
            // RangeError since we don't ship DOMException yet)
            // if the view is larger than 64 KiB.
            if (target.ByteLength > 65536)
            {
                JsThrow.RangeError(
                    "crypto.getRandomValues: quota exceeded (max 65536 bytes per call)");
                return null;
            }
            if (target.Kind == TypedArrayKind.Float32 ||
                target.Kind == TypedArrayKind.Float64)
            {
                JsThrow.TypeError(
                    "crypto.getRandomValues: floating-point typed arrays are not allowed");
                return null;
            }
            // Fill the backing bytes in-place using the OS
            // CSPRNG. Since we're writing directly into the
            // ArrayBuffer's bytes at the correct offset, the
            // TypedArray's view picks up the fresh data on
            // the next read without further fixup.
            var span = target.Buffer.Data.AsSpan(target.ByteOffset, target.ByteLength);
            RandomNumberGenerator.Fill(span);
            return target;
        }));

        crypto.SetNonEnumerable("randomUUID", new JsFunction("randomUUID", (thisVal, args) =>
        {
            // UUID v4 per RFC 4122 §4.4: 122 random bits,
            // four fixed version / variant bits at positions
            // 48–51 (version 4) and 64–65 (variant 10xx).
            // .NET's Guid.NewGuid returns a v4 GUID that
            // already respects those bits, so we can just
            // take its canonical hex form.
            return Guid.NewGuid().ToString("D").ToLowerInvariant();
        }));

        // SubtleCrypto — only `digest` is implemented in
        // this slice. sign / verify / encrypt / decrypt /
        // generateKey / importKey all require a real
        // CryptoKey object model that's deferred to a
        // future slice. digest is the commonly-used
        // primitive for content hashing (SRI integrity,
        // cache keys, dedup), and .NET ships the four
        // SHA variants out of the box.
        var subtle = new JsObject { Prototype = engine.ObjectPrototype };
        subtle.SetNonEnumerable("digest", new JsFunction("digest", (thisVal, args) =>
        {
            if (args.Count < 2)
            {
                JsThrow.TypeError("crypto.subtle.digest: expected (algorithm, data)");
                return null;
            }
            string algorithmName;
            if (args[0] is string s)
            {
                algorithmName = s;
            }
            else if (args[0] is JsObject alg && alg.Get("name") is string n)
            {
                algorithmName = n;
            }
            else
            {
                JsThrow.TypeError("crypto.subtle.digest: algorithm must be a string or object with name");
                return null;
            }
            byte[] input = ExtractBufferBytes(args[1]);
            byte[] hash;
            switch (algorithmName.ToUpperInvariant())
            {
                case "SHA-1":
                    hash = SHA1.HashData(input);
                    break;
                case "SHA-256":
                    hash = SHA256.HashData(input);
                    break;
                case "SHA-384":
                    hash = SHA384.HashData(input);
                    break;
                case "SHA-512":
                    hash = SHA512.HashData(input);
                    break;
                default:
                    var rejection = new JsPromise(engine);
                    var err = new JsObject();
                    err.Set("name", "NotSupportedError");
                    err.Set("message", $"Unsupported digest algorithm: {algorithmName}");
                    rejection.Reject(err);
                    return rejection;
            }
            var buf = new JsArrayBuffer(hash.Length);
            Array.Copy(hash, buf.Data, hash.Length);
            var promise = new JsPromise(engine);
            promise.Resolve(buf);
            return promise;
        }));
        crypto.SetNonEnumerable("subtle", subtle);

        engine.Globals["crypto"] = crypto;
    }

    /// <summary>
    /// Unwrap a JS value into its raw byte buffer. Accepts
    /// <see cref="JsArrayBuffer"/> (whole buffer),
    /// <see cref="JsTypedArray"/> (visible byte range), and
    /// <see cref="JsDataView"/> (subrange). Used by
    /// <c>subtle.digest</c> and future bulk operations.
    /// </summary>
    private static byte[] ExtractBufferBytes(object? source)
    {
        if (source is JsArrayBuffer ab)
        {
            var copy = new byte[ab.Data.Length];
            Array.Copy(ab.Data, copy, ab.Data.Length);
            return copy;
        }
        if (source is JsTypedArray ta)
        {
            var copy = new byte[ta.ByteLength];
            Array.Copy(ta.Buffer.Data, ta.ByteOffset, copy, 0, ta.ByteLength);
            return copy;
        }
        if (source is JsDataView dv)
        {
            var copy = new byte[dv.ByteLength];
            Array.Copy(dv.Buffer.Data, dv.ByteOffset, copy, 0, dv.ByteLength);
            return copy;
        }
        JsThrow.TypeError("crypto.subtle.digest: data must be a BufferSource");
        return null!;
    }
}
