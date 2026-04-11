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

        engine.Globals["crypto"] = crypto;
    }
}
